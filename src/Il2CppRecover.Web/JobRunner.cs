using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Il2CppRecover.Web;

public interface IAnalysisProcess
{
    Task<AnalysisProcessResult> RunAsync(AnalysisProcessRequest request, Action<string> onStderr, CancellationToken jobCancellation, CancellationToken hostStopping);
}

public sealed record AnalysisProcessRequest(string InputPath, string OutputDirectory, string? Abi, string? UnityVersion, TimeSpan Timeout);

public sealed record AnalysisProcessResult(string Outcome, int ExitCode, string Stderr);

public sealed class CliAnalysisProcess(StudioOptions options) : IAnalysisProcess
{
    public async Task<AnalysisProcessResult> RunAsync(AnalysisProcessRequest request, Action<string> onStderr, CancellationToken jobCancellation, CancellationToken hostStopping)
    {
        if (!File.Exists(options.CliDll))
            return new AnalysisProcessResult("finished", 4, "The analysis program is not available in this server build.");

        Directory.CreateDirectory(request.OutputDirectory);
        var requestFile = Path.Combine(Directory.GetParent(request.OutputDirectory)!.FullName, "worker-request.json");
        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(new WorkerPayload(request.InputPath, request.OutputDirectory, request.Abi, request.UnityVersion)), hostStopping);

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(options.CliDll);
        start.ArgumentList.Add("worker");
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestFile);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                try { onStderr(e.Data); }
                catch (Exception) { }
            }
        };
        if (!process.Start())
            return new AnalysisProcessResult("finished", 4, "The analysis process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobCancellation, hostStopping, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
        }

        try { await process.WaitForExitAsync(); }
        catch (Exception) { }

        var stderr = "";
        if (jobCancellation.IsCancellationRequested)
            return new AnalysisProcessResult("cancelled", 6, stderr);
        if (timeout.IsCancellationRequested)
            return new AnalysisProcessResult("timeout", 5, stderr);
        if (hostStopping.IsCancellationRequested)
            return new AnalysisProcessResult("stopped", 6, stderr);
        return new AnalysisProcessResult("finished", process.ExitCode, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record WorkerPayload(string Input, string Output, string? Abi, string? UnityVersion);
}

public sealed class JobRunner(StudioDatabase database, IAnalysisProcess process) : BackgroundService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public bool Cancel(string jobId)
    {
        if (database.TryCancelQueued(jobId))
            return true;
        if (_running.TryGetValue(jobId, out var source))
        {
            source.Cancel();
            return true;
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        database.FailInterrupted();
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = database.TryClaim();
            if (job is null)
            {
                try { await Task.Delay(200, stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            await RunJob(job, stoppingToken);
        }
    }

    private async Task RunJob(JobRecord job, CancellationToken stoppingToken)
    {
        var jobCancel = new CancellationTokenSource();
        _running[job.Id] = jobCancel;
        try
        {
            var settings = database.GetSettings();
            var project = database.GetProject(job.ProjectId);
            if (project is null)
            {
                database.Finish(job.Id, "Failed", "Failed", 4, "The project was deleted before analysis started.");
                return;
            }

            var stderr = new System.Text.StringBuilder();
            var result = await process.RunAsync(
                new AnalysisProcessRequest(project.AnalysisPath, job.OutputDir, job.RequestedAbi, job.SuppliedUnityVersion, TimeSpan.FromSeconds(settings.TimeoutSeconds)),
                line =>
                {
                    stderr.AppendLine(line);
                    if (!TryMap(line, out var state, out var stage, out var done, out var total))
                        return;
                    database.MarkProgress(job.Id, state, stage, done, total, line);
                },
                jobCancel.Token,
                stoppingToken);

            if (result.Outcome == "cancelled")
            {
                database.Finish(job.Id, "Cancelled", "Cancelled", 6, "Cancelled.");
                return;
            }
            if (result.Outcome == "timeout")
            {
                database.Finish(job.Id, "Failed", "Timed out", 5, "The analysis exceeded the time limit and was stopped.");
                return;
            }
            if (result.Outcome == "stopped")
            {
                database.Finish(job.Id, "Failed", "Stopped", 6, "The server stopped while this job was running. The package is still stored. Retry the analysis.");
                return;
            }

            if (result.ExitCode != 0)
            {
                database.Finish(job.Id, "Failed", "Failed", result.ExitCode, FailureMessage(job.OutputDir, stderr.ToString()));
                TryRememberInventory(job);
                return;
            }

            if (!File.Exists(Path.Combine(job.OutputDir, "recovery-report.json")))
            {
                database.Finish(job.Id, "Failed", "Failed", 4, "The analysis finished without a recovery report.");
                return;
            }

            TryRememberInventory(job);
            var report = JsonSerializer.Deserialize<RecoveryReport>(File.ReadAllText(Path.Combine(job.OutputDir, "recovery-report.json")), JsonDefaults.Options);
            var counts = report?.Counts;
            var limited = counts is null
                || counts.FullyRecoveredUniqueBodies == 0
                || counts.PlaceholderBodies > 0
                || counts.MetadataOnly > 0
                || counts.Unsupported > 0
                || counts.Failed > 0
                || counts.PartiallyRecovered > 0
                || counts.DisassembledOnly > 0;
            database.Finish(
                job.Id,
                limited ? "CompletedWithLimitations" : "Completed",
                limited ? "Finished with unrecovered methods" : "Finished",
                0,
                null);
        }
        catch (Exception)
        {
            database.Finish(job.Id, "Failed", "Failed", 4, "The analysis stopped because of an internal error.");
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
            jobCancel.Dispose();
        }
    }

    private void TryRememberInventory(JobRecord job)
    {
        var path = Path.Combine(job.OutputDir, "inventory.json");
        if (!File.Exists(path))
            return;
        try
        {
            var inventory = JsonSerializer.Deserialize<InventoryResult>(File.ReadAllText(path), JsonDefaults.Options);
            if (inventory is null)
                return;
            database.UpdateProjectSummary(
                job.ProjectId,
                inventory.UnityVersion.Version,
                inventory.UnityVersion.Source,
                inventory.SelectedBinary?.Architecture.ToString(),
                inventory.Metadata?.HeaderVersion,
                inventory.SelectedAbi);
        }
        catch (JsonException)
        {
        }
    }

    private static string FailureMessage(string outputDir, string stderr)
    {
        var path = Path.Combine(outputDir, "inventory.json");
        if (File.Exists(path))
        {
            try
            {
                var inventory = JsonSerializer.Deserialize<InventoryResult>(File.ReadAllText(path), JsonDefaults.Options);
                if (inventory?.Diagnostics.Count > 0)
                    return string.Join(" ", inventory.Diagnostics.Take(6));
            }
            catch (JsonException)
            {
            }
        }
        return Sanitize(stderr);
    }

    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "The package could not be analyzed.";
        var cleaned = PathPattern().Replace(text, "[path]");
        cleaned = string.Join(" ", cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8));
        if (cleaned.Length > 700)
            cleaned = cleaned[..700];
        return cleaned.Length == 0 ? "The package could not be analyzed." : cleaned;
    }

    internal static bool TryMap(string line, out string state, out string stage, out int? done, out int? total)
    {
        done = null;
        total = null;
        if (line.StartsWith("Methods ", StringComparison.Ordinal))
        {
            state = "AnalyzingNativeCode";
            stage = "Analyzing native code";
            var fraction = line["Methods ".Length..].Split(' ', ':')[0];
            var parts = fraction.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out var parsedDone) && int.TryParse(parts[1], out var parsedTotal))
            {
                done = parsedDone;
                total = parsedTotal;
                stage = "Analyzing native code (" + parsedDone + " of " + parsedTotal + " methods)";
            }
            return true;
        }
        if (line.StartsWith("Hash and inventory", StringComparison.Ordinal) || line.StartsWith("Inventory finished", StringComparison.Ordinal))
        {
            state = "Extracting";
            stage = "Extracting package files";
            return true;
        }
        if (line.StartsWith("Reading metadata", StringComparison.Ordinal) || line.StartsWith("Metadata tables", StringComparison.Ordinal))
        {
            state = "ReadingMetadata";
            stage = "Reading metadata";
            return true;
        }
        if (line.StartsWith("Loading binary", StringComparison.Ordinal) || line.StartsWith("LibCpp2IL", StringComparison.Ordinal) || line.StartsWith("Native loader", StringComparison.Ordinal))
        {
            state = "AnalyzingNativeCode";
            stage = "Analyzing native code";
            return true;
        }
        if (line.StartsWith("Writing output", StringComparison.Ordinal))
        {
            state = "GeneratingOutput";
            stage = "Generating output";
            return true;
        }
        state = "";
        stage = "";
        return false;
    }

    private static Regex PathPattern() => new(@"([A-Za-z]:\\[^\s""]+|/(?:home|tmp|workspace|Users|var|opt|usr)[^\s""]*)", RegexOptions.Compiled);
}

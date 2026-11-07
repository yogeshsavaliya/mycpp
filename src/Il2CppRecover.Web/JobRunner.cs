using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Il2CppRecover.Web;

public interface IAnalysisProcess
{
    Task<AnalysisProcessResult> RunAsync(AnalysisProcessRequest request, Action<string> onStderr, CancellationToken jobCancellation, CancellationToken hostStopping);
}

public sealed record AnalysisProcessRequest(string InputPath, string OutputDirectory, string? Abi, string? UnityVersion, TimeSpan Timeout, bool GameplayFirst = false, int MethodBudgetMs = 0);

public sealed record AnalysisProcessResult(string Outcome, int ExitCode, string Stderr);

public sealed class CliAnalysisProcess(StudioOptions options) : IAnalysisProcess
{
    public async Task<AnalysisProcessResult> RunAsync(AnalysisProcessRequest request, Action<string> onStderr, CancellationToken jobCancellation, CancellationToken hostStopping)
    {
        if (!File.Exists(options.CliDll))
            return new AnalysisProcessResult("finished", 4, "The analysis program is not available in this server build.");

        Directory.CreateDirectory(request.OutputDirectory);
        var requestFile = Path.Combine(Directory.GetParent(request.OutputDirectory)!.FullName, "worker-request.json");
        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(new WorkerPayload(request.InputPath, request.OutputDirectory, request.Abi, request.UnityVersion, request.GameplayFirst, request.MethodBudgetMs)), hostStopping);
        var stderrLog = new StreamWriter(new FileStream(Path.Combine(request.OutputDirectory, "worker-stderr.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var tail = new Queue<string>();

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
            if (e.Data is null)
                return;
            try
            {
                stderrLog.WriteLine(e.Data);
                tail.Enqueue(e.Data);
                while (tail.Count > 80)
                    tail.Dequeue();
                onStderr(e.Data);
            }
            catch (Exception ex)
            {
                try { stderrLog.WriteLine("progress callback failed: " + ex.GetType().Name + ": " + ex.Message); }
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

        var stderr = string.Join('\n', tail);
        stderrLog.Dispose();
        if (jobCancellation.IsCancellationRequested)
            return new AnalysisProcessResult("cancelled", 6, stderr);
        if (timeout.IsCancellationRequested)
            return new AnalysisProcessResult("timeout", 5, stderr);
        if (hostStopping.IsCancellationRequested)
            return new AnalysisProcessResult("stopped", 6, stderr);
        var code = process.HasExited ? process.ExitCode : -1;
        var crashed = code is < 0 or 134 or 137 or 139 || code > 128;
        return new AnalysisProcessResult(crashed ? "crashed" : "finished", code, stderr);
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

    private sealed record WorkerPayload(string Input, string Output, string? Abi, string? UnityVersion, bool GameplayFirst = false, int MethodBudgetMs = 0);
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

            var tail = new Queue<string>();
            int? lastDone = null;
            int? lastTotal = null;
            string? pendingState = null;
            string? pendingStage = null;
            int? pendingDone = null;
            int? pendingTotal = null;
            var pendingLog = new StringBuilder();
            var lastFlush = DateTime.UtcNow;
            var progressGate = new object();
            void FlushPending()
            {
                lock (progressGate)
                {
                    if (pendingLog.Length == 0 || pendingState is null || pendingStage is null)
                        return;
                    var chunk = pendingLog.ToString().TrimEnd('\n');
                    pendingLog.Clear();
                    database.MarkProgress(job.Id, pendingState, pendingStage, pendingDone, pendingTotal, chunk);
                    lastFlush = DateTime.UtcNow;
                }
            }
            using var flushTimer = new Timer(_ =>
            {
                try { FlushPending(); }
                catch (Exception) { }
            }, null, 500, 500);
            var gameplay = string.Equals(settings.BodyScope, "gameplay", StringComparison.Ordinal);
            var result = await process.RunAsync(
                new AnalysisProcessRequest(project.AnalysisPath, job.OutputDir, job.RequestedAbi, job.SuppliedUnityVersion, TimeSpan.FromSeconds(settings.TimeoutSeconds), gameplay, 0),
                line =>
                {
                    tail.Enqueue(line);
                    while (tail.Count > 80)
                        tail.Dequeue();
                    var mapped = TryMap(line, out var state, out var stage, out var done, out var total, out var keepCounts);
                    lock (progressGate)
                    {
                        pendingLog.Append(line).Append('\n');
                        if (mapped)
                        {
                            if (keepCounts)
                            {
                                done = lastDone;
                                total = lastTotal;
                            }
                            else if (done is not null || total is not null)
                            {
                                lastDone = done;
                                lastTotal = total;
                            }
                            pendingState = state;
                            pendingStage = stage;
                            pendingDone = done;
                            pendingTotal = total;
                        }
                        if (pendingState is not null && (!mapped || !keepCounts || (DateTime.UtcNow - lastFlush).TotalMilliseconds >= 400))
                            FlushPending();
                    }
                },
                jobCancel.Token,
                stoppingToken);
            FlushPending();

            if (result.Outcome == "cancelled")
            {
                database.Finish(job.Id, "Cancelled", "Cancelled", 6, "Cancelled.");
                return;
            }
            if (result.Outcome == "timeout")
            {
                database.Finish(job.Id, "TimedOut", "Timed out", 5, "The analysis exceeded the time limit and was stopped.");
                return;
            }
            if (result.Outcome == "stopped")
            {
                database.Finish(job.Id, "Failed", "Stopped", 6, "The server stopped while this job was running. The package is still stored. Retry the analysis.");
                return;
            }

            if (result.ExitCode != 0 || result.Outcome == "crashed")
            {
                var stderr = result.Stderr.Length > 0 ? result.Stderr : string.Join('\n', tail);
                database.Finish(job.Id, "Failed", "Failed", result.ExitCode, FailureMessage(job.OutputDir, stderr, result.ExitCode), stderr);
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
            var internalErrors = counts?.InternalErrors ?? 0;
            var limited = counts is null
                || counts.FullyRecoveredUniqueBodies == 0
                || counts.PlaceholderBodies > 0
                || counts.MetadataOnly > 0
                || counts.Unsupported > 0
                || counts.Failed > internalErrors
                || counts.PartiallyRecovered > 0
                || counts.DisassembledOnly > 0
                || counts.NotAnalyzed > 0
                || counts.AnalysisBudgetExceeded > 0
                || counts.TimedOut > 0;
            if (internalErrors > 0)
            {
                var samples = report?.InternalErrorSamples ?? [];
                database.Finish(job.Id, "CompletedWithInternalErrors", "Completed with internal errors", 0, internalErrors + " method" + (internalErrors == 1 ? "" : "s") + " failed internally. " + string.Join(" ", samples.Take(3)), string.Join("\n", samples));
            }
            else
            {
                database.Finish(
                    job.Id,
                    limited ? "CompletedWithLimitations" : "Completed",
                    limited ? "Finished with unrecovered methods" : "Finished",
                    0,
                    null);
            }
        }
        catch (Exception ex)
        {
            database.Finish(job.Id, "Failed", "Failed", 4, "The analysis stopped because of an internal error: " + ex.GetType().Name + ": " + ex.Message, ex.ToString());
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

    private static string FailureMessage(string outputDir, string stderr, int exitCode)
    {
        if (exitCode == 3)
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
        }
        return DescribeFailure(exitCode, stderr);
    }

    internal static string DescribeFailure(int exitCode, string? stderr)
    {
        var lines = (stderr ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => PathPattern().Replace(line, "[path]"))
            .ToList();
        string? picked = null;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("Exception", StringComparison.Ordinal)
                || line.StartsWith("TimedOut", StringComparison.Ordinal)
                || line.Contains("Stack overflow", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("AnalysisBudget", StringComparison.Ordinal)
                || line.StartsWith("Method start:", StringComparison.Ordinal)
                || line.StartsWith("Method end", StringComparison.Ordinal))
            {
                picked = line;
                break;
            }
        }
        if (picked is null)
            picked = lines.Count == 0 ? "The analysis process produced no error text." : string.Join(" | ", lines.TakeLast(6));
        var crashed = exitCode is < 0 or 134 or 137 or 139 || exitCode > 128;
        var prefix = crashed
            ? "The analysis process terminated unexpectedly (exit " + exitCode + "). "
            : "The analysis process exited with code " + exitCode + ". ";
        var text = prefix + picked;
        return text.Length > 700 ? text[..700] : text;
    }

    internal static string Sanitize(string? text) => DescribeFailure(4, text);

    internal static bool TryMap(string line, out string state, out string stage, out int? done, out int? total, out bool keepCounts)
    {
        done = null;
        total = null;
        keepCounts = false;
        if (line.StartsWith("Helpers ", StringComparison.Ordinal))
        {
            state = "AnalyzingNativeCode";
            keepCounts = true;
            stage = "Identifying runtime helpers";
            var fraction = Fraction(line["Helpers ".Length..]);
            if (fraction is { } pair)
                stage = "Identifying runtime helpers (" + pair.Done + " of " + pair.Total + " entries)";
            return true;
        }
        if (line.StartsWith("Heartbeat ", StringComparison.Ordinal))
        {
            state = "AnalyzingNativeCode";
            keepCounts = true;
            stage = line["Heartbeat ".Length..];
            return true;
        }
        if (line.StartsWith("Method start:", StringComparison.Ordinal))
        {
            state = "AnalyzingNativeCode";
            keepCounts = true;
            stage = "Body analysis: " + line["Method start:".Length..].Trim();
            return true;
        }
        if (line.StartsWith("Identity ", StringComparison.Ordinal))
        {
            state = "Extracting";
            stage = "Starting analysis";
            keepCounts = true;
            return true;
        }
        if (line.StartsWith("Framework bodies not analyzed", StringComparison.Ordinal) || line.StartsWith("Body analysis", StringComparison.Ordinal))
        {
            state = "AnalyzingNativeCode";
            stage = line;
            keepCounts = true;
            return true;
        }
        if (line.StartsWith("Methods ", StringComparison.Ordinal))
        {
            state = "AnalyzingNativeCode";
            stage = "Body analysis";
            var fraction = Fraction(line["Methods ".Length..]);
            if (fraction is { } pair)
            {
                done = pair.Done;
                total = pair.Total;
                stage = "Body analysis (" + pair.Done + " of " + pair.Total + " methods started)";
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
            keepCounts = true;
            return true;
        }
        state = "";
        stage = "";
        return false;
    }

    private static (int Done, int Total)? Fraction(string text)
    {
        foreach (var token in text.Split(' ', ':'))
        {
            var parts = token.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out var done) && int.TryParse(parts[1], out var total))
                return (done, total);
        }
        return null;
    }

    private static Regex PathPattern() => new(@"([A-Za-z]:\\[^\s""]+|/(?:home|tmp|workspace|Users|var|opt|usr)[^\s""]*)", RegexOptions.Compiled);
}

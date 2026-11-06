using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Il2CppRecover.Binary;
using Il2CppRecover.Tests.Helpers;
using Il2CppRecover.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Il2CppRecover.Web.Tests;

public class StudioWebTests
{
    [Fact]
    public async Task Capabilities_match_the_implemented_formats()
    {
        await using var factory = new StudioFactory();
        var client = await Open(factory);
        var body = await client.GetFromJsonAsync<JsonElement>("/api/capabilities");
        var formats = body.GetProperty("formats").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!, item => item.GetProperty("state").GetString());
        Assert.Equal("supported", formats["apk"]);
        Assert.Equal("supported", formats["xapk"]);
        Assert.Equal("unavailable", formats["aab"]);
        Assert.Contains("AAB support is not available yet", body.GetProperty("formats").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "aab").GetProperty("detail").GetString());
        var unsupported = body.GetProperty("notSupported").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("IPA", unsupported);
        Assert.Contains("WebAssembly", unsupported);
        Assert.Contains("GameAssembly.dll", unsupported);
        Assert.DoesNotContain(formats.Keys, key => key is "ipa" or "wasm" or "dll");
    }

    [Fact]
    public async Task Apk_import_runs_the_engine_and_export_contains_the_placeholder()
    {
        await using var factory = new StudioFactory();
        var client = await Open(factory);
        var apk = WriteApk();
        var created = await Upload(client, apk, "Sample");
        var jobId = created.GetProperty("job").GetProperty("id").GetString()!;
        var projectId = created.GetProperty("project").GetProperty("id").GetString()!;
        var job = await Wait(client, jobId);
        Assert.Equal("CompletedWithLimitations", job.GetProperty("state").GetString());
        Assert.DoesNotContain("%", job.GetProperty("stage").GetString());

        var methods = await client.GetFromJsonAsync<JsonElement>("/api/projects/" + projectId + "/methods");
        var method = methods.GetProperty("methods").EnumerateArray().Single();
        Assert.Equal("MetadataOnly", method.GetProperty("status").GetString());
        Assert.Equal("Metadata Only", method.GetProperty("statusLabel").GetString());
        Assert.True(method.GetProperty("placeholder").GetBoolean());
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/projects/" + projectId + "/methods/0");
        Assert.Equal("Method body not recovered.", detail.GetProperty("placeholderNotice").GetString());
        Assert.Contains("IL2CPP_RECOVER_UNRECOVERED", detail.GetProperty("csharp").GetString());
        Assert.Contains("has not been checked", detail.GetProperty("validationNotice").GetString());

        var report = await client.GetFromJsonAsync<JsonElement>("/api/projects/" + projectId + "/report");
        Assert.Equal(0, report.GetProperty("counts").GetProperty("fullyRecoveredUniqueBodies").GetInt32());
        Assert.Equal(1, report.GetProperty("counts").GetProperty("metadataOnly").GetInt32());

        var zipBytes = await client.GetByteArrayAsync("/api/projects/" + projectId + "/export/csharp");
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var source = zip.Entries.Single(entry => entry.FullName.EndsWith(".cs", StringComparison.Ordinal));
        using var reader = new StreamReader(source.Open());
        Assert.Contains("IL2CPP_RECOVER_UNRECOVERED", await reader.ReadToEndAsync());

        var full = await client.GetAsync("/api/projects/" + projectId + "/export/full");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("analysis-export.zip", full.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var missing = await client.GetAsync("/api/projects/" + projectId + "/export/not-a-path");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Invalid_and_unsupported_uploads_do_not_create_projects()
    {
        await using var factory = new StudioFactory();
        var client = await Open(factory);
        var text = Path.Combine(factory.DataRoot, "notes.txt");
        Directory.CreateDirectory(factory.DataRoot);
        await File.WriteAllTextAsync(text, "hello");
        var rejected = await UploadRaw(client, text, "notes.txt");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("zip archive", await rejected.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var elf = Path.Combine(factory.DataRoot, "libil2cpp.so");
        await File.WriteAllBytesAsync(elf, ElfBuilder.SharedObject(183));
        var so = await UploadRaw(client, elf, "libil2cpp.so");
        Assert.Equal(HttpStatusCode.BadRequest, so.StatusCode);
        Assert.Contains("global-metadata.dat", await so.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var aab = Path.Combine(factory.DataRoot, "game.aab");
        ApkBuilder.Write(aab, [("BundleConfig.pb", [1, 2, 3], CompressionLevel.NoCompression)]);
        var bundle = await UploadRaw(client, aab, "game.aab");
        Assert.Equal(HttpStatusCode.BadRequest, bundle.StatusCode);
        Assert.Contains("AAB support is not available yet", await bundle.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var projects = await client.GetFromJsonAsync<JsonElement>("/api/projects");
        Assert.Empty(projects.EnumerateArray());
    }

    [Fact]
    public async Task Nested_package_missing_split_is_rejected_by_the_api()
    {
        await using var factory = new StudioFactory();
        var client = await Open(factory);
        var root = Path.Combine(factory.DataRoot, "src");
        Directory.CreateDirectory(root);
        var baseApk = Path.Combine(root, "base.apk");
        ApkBuilder.Write(baseApk, [
            ("assets/bin/Data/Managed/Metadata/global-metadata.dat", MetadataFixture.Version31("Assembly-CSharp.dll", "Assembly-CSharp", "Game", "Player", "Jump", ["speed"], false), CompressionLevel.NoCompression)
        ]);
        var xapk = Path.Combine(root, "game.xapk");
        ApkBuilder.Write(xapk, [("base.apk", await File.ReadAllBytesAsync(baseApk), CompressionLevel.NoCompression)]);
        var response = await UploadRaw(client, xapk, "game.xapk");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("libil2cpp.so", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base.apk", text, StringComparison.Ordinal);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray());
    }

    [Fact]
    public async Task Cancel_stops_a_running_job()
    {
        var holding = new HoldingProcess();
        await using var factory = new StudioFactory(process: holding);
        var client = await Open(factory);
        var created = await Upload(client, WriteApk(), "Hold");
        var jobId = created.GetProperty("job").GetProperty("id").GetString()!;
        await holding.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancel = await client.PostAsync("/api/jobs/" + jobId + "/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = await Wait(client, jobId);
        Assert.Equal("Cancelled", cancelled.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Retry_runs_the_engine_again_for_the_same_project()
    {
        await using var factory = new StudioFactory();
        var client = await Open(factory);
        var created = await Upload(client, WriteApk(), "Retry");
        var first = created.GetProperty("job").GetProperty("id").GetString()!;
        var projectId = created.GetProperty("project").GetProperty("id").GetString()!;
        Assert.Equal("CompletedWithLimitations", (await Wait(client, first)).GetProperty("state").GetString());
        var retry = await client.PostAsync("/api/jobs/" + first + "/retry", null);
        var retryText = await retry.Content.ReadAsStringAsync();
        Assert.True(retry.IsSuccessStatusCode, retryText);
        var second = JsonDocument.Parse(retryText).RootElement.GetProperty("id").GetString()!;
        Assert.Equal("CompletedWithLimitations", (await Wait(client, second)).GetProperty("state").GetString());
        var jobs = await client.GetFromJsonAsync<JsonElement>("/api/jobs");
        Assert.Equal(2, jobs.EnumerateArray().Count());
        var methods = await client.GetFromJsonAsync<JsonElement>("/api/projects/" + projectId + "/methods");
        Assert.Equal("Jump", methods.GetProperty("methods").EnumerateArray().Single().GetProperty("name").GetString());
    }

    [Fact]
    public void Crash_stderr_names_the_exception_and_keeps_helper_progress_off_the_method_counter()
    {
        var stderr = """
            Hash and inventory
            Inventory finished: backend=Il2Cpp, succeeded=True
            Reading metadata tables
            Metadata tables finished: 30358 methods, layout=31.1
            Loading binary with pinned LibCpp2IL
            LibCpp2IL loaded the binary and metadata.
            Helpers Identifying runtime helpers 10/100: pass 1
            Method start: Assembly-CSharp Game.BodypartController::Update index=51 addr=0x1000
            System.InvalidOperationException: lift failed at 0x1000
            """;
        var message = JobRunner.DescribeFailure(139, stderr);
        Assert.Contains("139", message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Hash and inventory", message, StringComparison.Ordinal);
        Assert.True(JobRunner.TryMap("Helpers Identifying runtime helpers 10/100: pass 1", out _, out var stage, out var done, out _, out var keep));
        Assert.Null(done);
        Assert.True(keep);
        Assert.Contains("10 of 100", stage, StringComparison.Ordinal);
        Assert.True(JobRunner.TryMap("Methods 50/30358: body analysis started", out _, out var body, out var bodyDone, out var bodyTotal, out var keepBody));
        Assert.False(keepBody);
        Assert.Equal(50, bodyDone);
        Assert.Equal(30358, bodyTotal);
        Assert.Contains("Body analysis", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crashed_worker_fails_the_job_with_the_exit_code()
    {
        await using var factory = new StudioFactory(process: new CrashingProcess());
        var client = await Open(factory);
        var created = await Upload(client, WriteApk(), "Crash");
        var jobId = created.GetProperty("job").GetProperty("id").GetString()!;
        var job = await Wait(client, jobId);
        Assert.Equal("Failed", job.GetProperty("state").GetString());
        var error = job.GetProperty("error").GetString() ?? "";
        Assert.Contains("139", error, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Hash and inventory", error, StringComparison.Ordinal);
        Assert.Contains("BodypartController", job.GetProperty("log").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fast_progress_keeps_the_identity_line_in_the_job_log()
    {
        await using var factory = new StudioFactory(process: new BurstProcess());
        var client = await Open(factory);
        var created = await Upload(client, WriteApk(), "Burst");
        var jobId = created.GetProperty("job").GetProperty("id").GetString()!;
        var job = await Wait(client, jobId);
        var log = job.GetProperty("log").GetString() ?? "";
        Assert.Contains("Identity build=test pid=1 sha256=abc abi=arm64-v8a", log, StringComparison.Ordinal);
        Assert.Contains("Methods 50/4: body analysis started", log, StringComparison.Ordinal);
        Assert.False(job.GetProperty("bodyProgress").GetBoolean());
    }

    [Fact]
    public async Task Restart_marks_an_interrupted_job_failed_and_keeps_the_project()
    {
        var data = Path.Combine(Path.GetTempPath(), "il2cpp-studio-restart-" + Guid.NewGuid().ToString("N"));
        string jobId;
        await using (var factory = new StudioFactory(data, preserve: true))
        {
            var client = await Open(factory);
            var created = await Upload(client, WriteApk(), "Restart");
            jobId = created.GetProperty("job").GetProperty("id").GetString()!;
            await Wait(client, jobId);
        }

        await using (var sqlite = new SqliteConnection("Data Source=" + Path.Combine(data, "studio.db")))
        {
            await sqlite.OpenAsync();
            await using var update = sqlite.CreateCommand();
            update.CommandText = "UPDATE jobs SET state = 'AnalyzingNativeCode', stage = 'Analyzing native code', finished_utc = NULL WHERE id = $id;";
            update.Parameters.AddWithValue("$id", jobId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await using var restarted = new StudioFactory(data);
        var again = await Open(restarted);
        JsonElement job = default;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            job = await again.GetFromJsonAsync<JsonElement>("/api/jobs/" + jobId);
            if (job.GetProperty("state").GetString() == "Failed")
                break;
            await Task.Delay(200);
        }
        Assert.Equal("Failed", job.GetProperty("state").GetString());
        Assert.Contains("server stopped", job.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single((await again.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray());
    }

    private static string WriteApk()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2cpp-studio-apk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var apk = Path.Combine(root, "game.apk");
        ApkBuilder.Write(apk, [
            ("lib/arm64-v8a/libil2cpp.so", ElfBuilder.SharedObject(183), CompressionLevel.NoCompression),
            ("assets/bin/Data/Managed/Metadata/global-metadata.dat", MetadataFixture.Version31("Assembly-CSharp.dll", "Assembly-CSharp", "Game", "Player", "Jump", ["speed"], false), CompressionLevel.NoCompression),
            ("assets/bin/Data/globalgamemanagers", ApkBuilder.GlobalGameManagers("2022.3.62f3"), CompressionLevel.NoCompression)
        ]);
        return apk;
    }

    private static async Task<HttpClient> Open(StudioFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-Studio-Token", session.GetProperty("token").GetString());
        return client;
    }

    private static async Task<JsonElement> Upload(HttpClient client, string path, string name)
    {
        var response = await UploadRaw(client, path, Path.GetFileName(path), name);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> UploadRaw(HttpClient client, string path, string fileName, string? name = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(File.ReadAllBytes(path)), "file", fileName);
        content.Add(new StringContent(name ?? Path.GetFileNameWithoutExtension(fileName)), "name");
        return client.PostAsync("/api/projects", content);
    }

    private static async Task<JsonElement> Wait(HttpClient client, string jobId)
    {
        for (var i = 0; i < 80; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>("/api/jobs/" + jobId);
            if (IsTerminal(job.GetProperty("state").GetString()!))
                return job;
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException("The job did not finish.");
    }

    private static bool IsTerminal(string state) =>
        state is "Completed" or "CompletedWithLimitations" or "Failed" or "Cancelled";

    private sealed class CrashingProcess : IAnalysisProcess
    {
        public Task<AnalysisProcessResult> RunAsync(AnalysisProcessRequest request, Action<string> onStderr, CancellationToken jobCancellation, CancellationToken hostStopping)
        {
            foreach (var line in new[]
            {
                "Hash and inventory",
                "Inventory finished: backend=Il2Cpp, succeeded=True",
                "Reading metadata tables",
                "Metadata tables finished: 30358 methods, layout=31.1",
                "Loading binary with pinned LibCpp2IL",
                "LibCpp2IL loaded the binary and metadata.",
                "Helpers Identifying runtime helpers 10/100: pass 1",
                "Method start: Assembly-CSharp Game.BodypartController::Update index=51 addr=0x1000",
                "System.InvalidOperationException: lift failed at 0x1000"
            })
                onStderr(line);
            return Task.FromResult(new AnalysisProcessResult("crashed", 139, "System.InvalidOperationException: lift failed at 0x1000"));
        }
    }

    private sealed class BurstProcess : IAnalysisProcess
    {
        public Task<AnalysisProcessResult> RunAsync(AnalysisProcessRequest request, Action<string> onStderr, CancellationToken jobCancellation, CancellationToken hostStopping)
        {
            foreach (var line in new[]
            {
                "Hash and inventory",
                "Inventory finished: backend=Il2Cpp, succeeded=True",
                "Identity build=test pid=1 sha256=abc abi=arm64-v8a",
                "Reading metadata tables",
                "Metadata tables finished: 4 methods, layout=31.1",
                "Helpers Identifying runtime helpers 1/1: pass 1",
                "Methods 50/4: body analysis started",
                "Writing output"
            })
                onStderr(line);
            return Task.FromResult(new AnalysisProcessResult("finished", 0, ""));
        }
    }

    private sealed class HoldingProcess : IAnalysisProcess
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AnalysisProcessResult> RunAsync(AnalysisProcessRequest request, Action<string> onStderr, CancellationToken jobCancellation, CancellationToken hostStopping)
        {
            Started.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobCancellation, hostStopping);
            try
            {
                await Task.Delay(Timeout.Infinite, linked.Token);
            }
            catch (OperationCanceledException)
            {
            }
            _ = onStderr;
            return jobCancellation.IsCancellationRequested
                ? new AnalysisProcessResult("cancelled", 6, "")
                : new AnalysisProcessResult("stopped", 6, "");
        }
    }
}

public sealed class StudioFactory : WebApplicationFactory<Program>
{
    public StudioFactory(string? dataRoot = null, IAnalysisProcess? process = null, bool preserve = false)
    {
        DataRoot = dataRoot ?? Path.Combine(Path.GetTempPath(), "il2cpp-studio-" + Guid.NewGuid().ToString("N"));
        Process = process;
        Preserve = preserve;
    }

    public string DataRoot { get; }
    public IAnalysisProcess? Process { get; }
    public bool Preserve { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Studio:DataRoot", DataRoot);
        builder.UseSetting("Studio:FixedUrl", "false");
        builder.UseSetting("Studio:CliDll", Path.Combine(AppContext.BaseDirectory, "il2cpp-recover.dll"));
        if (Process is not null)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.Single(service => service.ServiceType == typeof(IAnalysisProcess));
                services.Remove(existing);
                services.AddSingleton(Process);
            });
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Preserve && Directory.Exists(DataRoot))
            Directory.Delete(DataRoot, recursive: true);
    }
}

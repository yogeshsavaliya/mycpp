using System.Diagnostics;
using Il2CppRecover.Tests.Helpers;
using Il2CppRecover.Worker;

namespace Il2CppRecover.Tests;

public class CliAndWorkerTests
{
    [Fact]
    public void Cli_inventory_prints_the_detected_metadata_version()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2cpprecover-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var apk = Path.Combine(root, "game.apk");
        ApkBuilder.Write(apk, [
            ("lib/arm64-v8a/libil2cpp.so", ElfBuilder.SharedObject(183), CompressionLevel.NoCompression),
            ("assets/bin/Data/Managed/Metadata/global-metadata.dat", MetadataFixture.Version31("Assembly-CSharp.dll", "Assembly-CSharp", "Game", "Player", "Jump", ["speed"], false), CompressionLevel.NoCompression),
            ("assets/bin/Data/globalgamemanagers", ApkBuilder.GlobalGameManagers("2022.3.62f3"), CompressionLevel.NoCompression)
        ]);
        var dll = Path.Combine(AppContext.BaseDirectory, "il2cpp-recover.dll");
        Assert.True(File.Exists(dll), dll);
        var output = Path.Combine(root, "out");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "exec", dll, "inventory", apk, "--output", output },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("\"metadataVersion\": 31", stdout);
        Assert.Contains("2022.3.62f3", stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        Assert.True(File.Exists(Path.Combine(output, "inventory.json")));
    }

    [Fact]
    public void Recover_slice_command_emits_real_csharp()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2cpprecover-slice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var code = Path.Combine(root, "add.bin");
        File.WriteAllBytes(code, Arm64Asm.Words(Arm64Asm.AddReg(0, 0, 1), Arm64Asm.Ret()));
        var dll = Path.Combine(AppContext.BaseDirectory, "il2cpp-recover.dll");
        var output = Path.Combine(root, "out");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                "exec", dll, "recover-slice", "--code", code, "--address", "1000", "--name", "Add",
                "--type", "ArithmeticFixture", "--namespace", "RecoveryFixtures", "--static",
                "--return", "int32", "--params", "a:int32,b:int32", "--output", output
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("RecoveredUnvalidated", stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        var source = File.ReadAllText(Path.Combine(output, "ArithmeticFixture.cs"));
        Assert.Equal(7, RoslynRunner.Invoke(source, "RecoveryFixtures.ArithmeticFixture", "Add", null, 3, 4));
    }

    [Fact]
    public void Worker_timeout_kills_a_sleeping_process()
    {
        var start = new ProcessStartInfo { FileName = "sleep", ArgumentList = { "30" } };
        var code = WorkerHost.Run(start, TimeSpan.FromSeconds(1), CancellationToken.None, out _, out _);
        Assert.Equal(WorkerHost.ExitTimeout, code);
    }
}

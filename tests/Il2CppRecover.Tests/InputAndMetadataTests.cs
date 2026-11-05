using Il2CppRecover.EngineAdapter;
using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

public class InputAndMetadataTests
{
    [Fact]
    public void Arm64_apk_inventory_keeps_the_version_string_and_hashes()
    {
        var root = Temp();
        var apk = Path.Combine(root, "game.apk");
        var metadata = MetadataFixture.Version31("Assembly-CSharp.dll", "Assembly-CSharp", "RecoveryFixtures", "ArithmeticFixture", "Add", ["a", "b"], true);
        ApkBuilder.Write(apk, [
            ("lib/arm64-v8a/libil2cpp.so", ElfBuilder.SharedObject(183), CompressionLevel.NoCompression),
            ("assets/bin/Data/Managed/Metadata/global-metadata.dat", metadata, CompressionLevel.NoCompression),
            ("assets/bin/Data/globalgamemanagers", ApkBuilder.GlobalGameManagers("2022.3.62f3"), CompressionLevel.NoCompression)
        ]);
        var before = Hashing.Sha256File(apk);
        var output = Path.Combine(root, "out");
        var result = AnalysisPipeline.Analyze(apk, output, new AnalysisOptions { SuppliedUnityVersion = "2021.3.0f1" }, null, CancellationToken.None);
        Assert.Equal(before, Hashing.Sha256File(apk));
        Assert.Equal(before, result.Inventory.InputSha256);
        Assert.True(result.Inventory.Succeeded);
        Assert.Equal(ScriptingBackend.Il2Cpp, result.Inventory.ScriptingBackend);
        Assert.Equal("2022.3.62f3", result.Inventory.UnityVersion.Version);
        Assert.Equal("globalgamemanagers+0x14", result.Inventory.UnityVersion.Source);
        Assert.Contains(result.Inventory.Diagnostics, d => d.Contains("2021.3.0f1", StringComparison.Ordinal));
        Assert.Equal(31, result.Inventory.Metadata!.HeaderVersion);
        Assert.Equal(NativeArchitecture.Arm64, result.Inventory.SelectedBinary!.Architecture);
        Assert.Equal("arm64-v8a", result.Inventory.SelectedAbi);
        Assert.NotNull(result.Catalog);
        var method = Assert.Single(Assert.Single(Assert.Single(result.Catalog!.Images).Types).Methods);
        Assert.Equal("Add", method.Name);
        Assert.Equal("a", method.Parameters[0].Name);
        Assert.Equal("b", method.Parameters[1].Name);
        Assert.True(method.IsStatic);
        Assert.Equal("unresolved", method.ReturnType);
        Assert.Equal(MethodRecoveryStatus.MetadataOnly, Assert.Single(result.Methods).Status);
        Assert.Equal(0, result.Report.Counts.FullyRecoveredUniqueBodies);
        Assert.Contains("not added to that numerator", result.Report.Counts.Denominator);
        Assert.Contains("LibCpp2IL", result.EngineLoadDiagnostic);
        Assert.True(File.Exists(Path.Combine(output, "inventory.json")));
        Assert.True(File.Exists(Path.Combine(output, "recovery-report.json")));
        Assert.True(File.Exists(Path.Combine(output, "csharp", "Assembly-CSharp.dll", "RecoveryFixtures", "ArithmeticFixture.cs")));
        var csharp = File.ReadAllText(Path.Combine(output, "csharp", "Assembly-CSharp.dll", "RecoveryFixtures", "ArithmeticFixture.cs"));
        Assert.Contains("IL2CPP_RECOVER_UNRECOVERED", csharp);
        Assert.DoesNotContain("FullyRecovered", csharp);
    }

    [Fact]
    public void Protected_metadata_does_not_receive_a_version()
    {
        var root = Temp();
        var apk = Path.Combine(root, "protected.apk");
        ApkBuilder.Write(apk, [
            ("lib/arm64-v8a/libil2cpp.so", ElfBuilder.SharedObject(183), CompressionLevel.NoCompression),
            ("assets/bin/Data/Managed/Metadata/global-metadata.dat", [1, 2, 3, 4, 5, 6, 7, 8], CompressionLevel.NoCompression)
        ]);
        var inventory = ApkArchive.Inspect(apk, Path.Combine(root, "out"), new ExtractionLimits(), null, null);
        Assert.False(inventory.Succeeded);
        Assert.False(inventory.Metadata!.MagicValid);
        Assert.Null(inventory.Metadata.HeaderVersion);
        Assert.True(inventory.Metadata.SuspectedProtection);
    }

    [Fact]
    public void Mono_and_wrong_architecture_are_explicit()
    {
        var root = Temp();
        var mono = Path.Combine(root, "mono.apk");
        ApkBuilder.Write(mono, [("assets/bin/Data/Managed/Assembly-CSharp.dll", [0x4D, 0x5A, 0, 0], CompressionLevel.NoCompression)]);
        var monoInventory = ApkArchive.Inspect(mono, Path.Combine(root, "mono-out"), new ExtractionLimits(), null, null);
        Assert.Equal(ScriptingBackend.Mono, monoInventory.ScriptingBackend);
        Assert.False(monoInventory.Succeeded);

        var x64 = Path.Combine(root, "x64.apk");
        var metadata = MetadataFixture.Version31("Assembly-CSharp.dll", "Assembly-CSharp", "", "Program", "Main", [], true);
        ApkBuilder.Write(x64, [
            ("lib/x86_64/libil2cpp.so", ElfBuilder.SharedObject(62), CompressionLevel.NoCompression),
            ("assets/bin/Data/Managed/Metadata/global-metadata.dat", metadata, CompressionLevel.NoCompression)
        ]);
        var inventory = ApkArchive.Inspect(x64, Path.Combine(root, "x64-out"), new ExtractionLimits(), null, null);
        Assert.Equal(NativeArchitecture.X64, inventory.SelectedBinary!.Architecture);
        Assert.False(inventory.Succeeded);
    }

    [Fact]
    public void Path_traversal_and_oversized_entries_are_rejected()
    {
        var root = Temp();
        var apk = Path.Combine(root, "bad.apk");
        ApkBuilder.Write(apk, [
            ("../outside.txt", [1, 2, 3, 4], CompressionLevel.NoCompression),
            ("lib/arm64-v8a/libil2cpp.so", new byte[80 * 1024], CompressionLevel.Optimal)
        ]);
        var inventory = ApkArchive.Inspect(apk, Path.Combine(root, "out"), ExtractionLimits.StrictTestDefaults(), null, null);
        Assert.Contains(inventory.Entries, e => e.Rejected && e.Name.Contains("..", StringComparison.Ordinal));
        Assert.Contains(inventory.Diagnostics, d => d.Contains("limit", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(root)!.FullName, "outside.txt")));
    }

    [Fact]
    public void Header_24_without_a_unity_version_is_not_guessed()
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes, 0xFAB11BAF);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), 24);
        var catalog = MetadataCatalogReader.Read(bytes, null, null);
        Assert.Equal(24, catalog.HeaderVersion);
        Assert.Empty(catalog.Images);
        Assert.Contains(catalog.Diagnostics, d => d.Contains("was not", StringComparison.OrdinalIgnoreCase) || d.Contains("not parsed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Synthetic_elf_does_not_crash_the_pinned_loader()
    {
        var metadata = MetadataFixture.Version31("Assembly-CSharp.dll", "Assembly-CSharp", "N", "T", "M", ["a"], true);
        var result = Cpp2IlSession.TryLoad(ElfBuilder.SharedObject(183), metadata, "2022.3.62f3");
        Assert.False(result.Loaded);
        Assert.Contains("LibCpp2IL", result.Diagnostic);
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "il2cpprecover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

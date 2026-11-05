using Il2CppRecover.Binary;
using Il2CppRecover.Input;
using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

public class PackageSetReaderTests
{
    [Fact]
    public void Single_apk_is_ready_without_copying()
    {
        var root = Temp();
        var apk = Path.Combine(root, "game.apk");
        WriteUnityApk(apk);
        var prepared = PackageSetReader.Prepare(apk, Path.Combine(root, "work"), new ExtractionLimits());
        Assert.Equal("ready", prepared.State);
        Assert.Equal("apk", prepared.Kind);
        Assert.Equal(Path.GetFullPath(apk), prepared.AnalysisPath);
        Assert.False(File.Exists(Path.Combine(root, "work", "normalized.apk")));
    }

    [Fact]
    public void Xapk_combines_base_metadata_and_arm64_split()
    {
        var root = Temp();
        var xapk = Path.Combine(root, "game.xapk");
        WriteNested(xapk, includeSplit: true, conflict: false);
        var prepared = PackageSetReader.Prepare(xapk, Path.Combine(root, "work"), new ExtractionLimits());
        Assert.Equal("ready", prepared.State);
        Assert.Equal("xapk", prepared.Kind);
        Assert.NotNull(prepared.Origins);
        Assert.Contains(prepared.Origins!.Files, f => f.Role == "libil2cpp" && f.Origin.Contains("config.arm64_v8a.apk!", StringComparison.Ordinal));
        Assert.Contains(prepared.Origins.Files, f => f.Role == "global-metadata" && f.Origin.StartsWith("base.apk!", StringComparison.Ordinal));
        Assert.True(File.Exists(prepared.AnalysisPath));
        var inventory = ApkArchive.Inspect(prepared.AnalysisPath!, Path.Combine(root, "inv"), new ExtractionLimits(), null, null);
        Assert.True(inventory.Succeeded);
        Assert.Equal("2022.3.62f3", inventory.UnityVersion.Version);
        Assert.Equal(31, inventory.Metadata?.HeaderVersion);
    }

    [Fact]
    public void Renaming_a_nested_package_to_apk_does_not_skip_the_split_check()
    {
        var root = Temp();
        var renamed = Path.Combine(root, "renamed.apk");
        WriteNested(renamed, includeSplit: true, conflict: false);
        var prepared = PackageSetReader.Prepare(renamed, Path.Combine(root, "work"), new ExtractionLimits());
        Assert.Equal("ready", prepared.State);
        Assert.Equal("nested", prepared.Kind);
        Assert.Contains(prepared.Origins!.Files, f => f.Origin.Contains('!'));
    }

    [Fact]
    public void Missing_split_is_rejected()
    {
        var root = Temp();
        var xapk = Path.Combine(root, "partial.xapk");
        WriteNested(xapk, includeSplit: false, conflict: false);
        var prepared = PackageSetReader.Prepare(xapk, Path.Combine(root, "work"), new ExtractionLimits());
        Assert.Equal("rejected", prepared.State);
        Assert.Contains("libil2cpp.so", prepared.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base.apk", prepared.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "work", "normalized.apk")));
    }

    [Fact]
    public void Conflicting_arm64_binaries_are_rejected()
    {
        var root = Temp();
        var apks = Path.Combine(root, "game.apks");
        WriteNested(apks, includeSplit: true, conflict: true);
        var prepared = PackageSetReader.Prepare(apks, Path.Combine(root, "work"), new ExtractionLimits());
        Assert.Equal("rejected", prepared.State);
        Assert.Contains("Conflicting", prepared.Message, StringComparison.Ordinal);
        Assert.Contains("config.arm64_v8a.apk", prepared.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_extraction_limit_stops_the_package()
    {
        var root = Temp();
        var xapk = Path.Combine(root, "big.xapk");
        var huge = Path.Combine(root, "huge.apk");
        ApkBuilder.Write(huge, [
            ("lib/arm64-v8a/libil2cpp.so", new byte[80 * 1024], System.IO.Compression.CompressionLevel.NoCompression)
        ]);
        ApkBuilder.Write(xapk, [
            ("huge.apk", File.ReadAllBytes(huge), System.IO.Compression.CompressionLevel.NoCompression)
        ]);
        var prepared = PackageSetReader.Prepare(xapk, Path.Combine(root, "work"), ExtractionLimits.StrictTestDefaults());
        Assert.Equal("rejected", prepared.State);
        Assert.Contains("limit", prepared.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "outside.so")));
    }

    [Fact]
    public void Unsafe_nested_path_is_not_extracted()
    {
        var root = Temp();
        var xapk = Path.Combine(root, "slip.xapk");
        var inner = Path.Combine(root, "inner.apk");
        ApkBuilder.Write(inner, [
            ("../outside.so", [1, 2, 3, 4], System.IO.Compression.CompressionLevel.NoCompression)
        ]);
        ApkBuilder.Write(xapk, [
            ("inner.apk", File.ReadAllBytes(inner), System.IO.Compression.CompressionLevel.NoCompression)
        ]);
        var prepared = PackageSetReader.Prepare(xapk, Path.Combine(root, "work"), new ExtractionLimits());
        Assert.Equal("rejected", prepared.State);
        Assert.Contains("unsafe", prepared.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "outside.so")));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(root)!.FullName, "outside.so")));
    }

    [Fact]
    public void Aab_is_unavailable_even_when_the_extension_is_correct()
    {
        var root = Temp();
        var aab = Path.Combine(root, "game.aab");
        ApkBuilder.Write(aab, [
            ("BundleConfig.pb", [1, 2, 3], System.IO.Compression.CompressionLevel.NoCompression),
            ("base/manifest/AndroidManifest.xml", [4], System.IO.Compression.CompressionLevel.NoCompression)
        ]);
        var prepared = PackageSetReader.Prepare(aab, Path.Combine(root, "work"), new ExtractionLimits());
        Assert.Equal("unavailable", prepared.State);
        Assert.Equal("aab", prepared.Kind);
        Assert.Contains("AAB support is not available yet", prepared.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Standalone_elf_is_not_a_supported_package()
    {
        var root = Temp();
        var so = Path.Combine(root, "libil2cpp.so");
        File.WriteAllBytes(so, ElfBuilder.SharedObject(183));
        var prepared = PackageSetReader.Prepare(so, Path.Combine(root, "work"), new ExtractionLimits());
        Assert.Equal("rejected", prepared.State);
        Assert.Contains("global-metadata.dat", prepared.Message, StringComparison.Ordinal);
    }

    private static void WriteNested(string path, bool includeSplit, bool conflict)
    {
        var dir = Path.GetDirectoryName(path)!;
        var baseApk = Path.Combine(dir, "base-src.apk");
        WriteUnityApk(baseApk, includeNative: false);
        var entries = new List<(string Name, byte[] Bytes, System.IO.Compression.CompressionLevel Level)>
        {
            ("base.apk", File.ReadAllBytes(baseApk), System.IO.Compression.CompressionLevel.NoCompression)
        };
        if (includeSplit)
        {
            var split = Path.Combine(dir, "split-src.apk");
            ApkBuilder.Write(split, [
                ("lib/arm64-v8a/libil2cpp.so", ElfBuilder.SharedObject(183, [1, 2, 3, 4]), System.IO.Compression.CompressionLevel.NoCompression)
            ]);
            entries.Add(("config.arm64_v8a.apk", File.ReadAllBytes(split), System.IO.Compression.CompressionLevel.NoCompression));
        }
        if (conflict)
        {
            var other = Path.Combine(dir, "split-b.apk");
            ApkBuilder.Write(other, [
                ("lib/arm64-v8a/libil2cpp.so", ElfBuilder.SharedObject(183, [9, 9, 9, 9]), System.IO.Compression.CompressionLevel.NoCompression)
            ]);
            entries.Add(("config.arm64_v8a.2.apk", File.ReadAllBytes(other), System.IO.Compression.CompressionLevel.NoCompression));
        }
        ApkBuilder.Write(path, entries);
    }

    private static void WriteUnityApk(string path, bool includeNative = true)
    {
        var entries = new List<(string, byte[], System.IO.Compression.CompressionLevel)>
        {
            ("assets/bin/Data/Managed/Metadata/global-metadata.dat", MetadataFixture.Version31("Assembly-CSharp.dll", "Assembly-CSharp", "Game", "Player", "Jump", ["speed"], false), System.IO.Compression.CompressionLevel.NoCompression),
            ("assets/bin/Data/globalgamemanagers", ApkBuilder.GlobalGameManagers("2022.3.62f3"), System.IO.Compression.CompressionLevel.NoCompression)
        };
        if (includeNative)
            entries.Add(("lib/arm64-v8a/libil2cpp.so", ElfBuilder.SharedObject(183), System.IO.Compression.CompressionLevel.NoCompression));
        ApkBuilder.Write(path, entries);
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "il2cpprecover-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

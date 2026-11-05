using System.IO.Compression;
using System.Text.Json;

namespace Il2CppRecover.Input;

public sealed class PackageOriginFile
{
    public required string Role { get; init; }
    public required string NormalizedPath { get; init; }
    public required string Origin { get; init; }
    public required string Sha256 { get; init; }
    public long SizeBytes { get; init; }
}

public sealed class PackageMemberInfo
{
    public required string EntryName { get; init; }
    public required string Sha256 { get; init; }
    public long SizeBytes { get; init; }
}

public sealed class PackageOrigins
{
    public required string Kind { get; init; }
    public IReadOnlyList<PackageMemberInfo> Members { get; init; } = [];
    public IReadOnlyList<PackageOriginFile> Files { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class PreparedPackage
{
    public required string Kind { get; init; }
    public required string State { get; init; }
    public string? Message { get; init; }
    public string? AnalysisPath { get; init; }
    public PackageOrigins? Origins { get; init; }
}

/// <summary>
/// Decides whether an upload is one APK, a nested XAPK/APKS/APKM set, or an
/// unavailable Android App Bundle. Detection uses archive contents. The
/// original file is not modified. A nested set is copied into one APK the
/// existing pipeline already knows how to read, with an origin for every file.
/// </summary>
public static class PackageSetReader
{
    public const string AabMessage = "AAB support is not available yet. An Android App Bundle has to be converted into APKs with bundletool before this workbench can read it. bundletool is not included, and this file was not converted.";

    public static PreparedPackage Prepare(string inputPath, string workDirectory, ExtractionLimits limits, string? requestedAbi = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            return Reject("unknown", "The file does not exist.");

        var length = new FileInfo(inputPath).Length;
        if (length > limits.MaxInputBytes)
            return Reject("unknown", "This file is " + length + " bytes, which is above the limit of " + limits.MaxInputBytes + " bytes.");

        var extension = Path.GetExtension(inputPath);
        var header = ReadHeader(inputPath);
        if (IsElf(header))
            return Reject("elf", "A standalone native binary is not enough. Analysis needs the matching global-metadata.dat from the same Unity build.");
        if (IsPe(header))
            return Reject("pe", "GameAssembly.dll and other Windows executables are not supported in this version.");
        if (IsWasm(header))
            return Reject("wasm", "WebAssembly packages are not supported in this version.");
        if (extension.Equals(".ipa", StringComparison.OrdinalIgnoreCase))
            return Reject("ipa", "IPA packages are not supported in this version.");
        if (!IsZip(header))
        {
            if (extension.Equals(".aab", StringComparison.OrdinalIgnoreCase))
                return Unavailable(AabMessage);
            return Reject("unknown", "This file is not a zip archive. Import an APK, or a nested XAPK, APKS, or APKM package.");
        }

        List<ZipName> entries;
        try
        {
            entries = ListEntries(inputPath, limits);
        }
        catch (InputRejectedException ex)
        {
            return Reject(KindFromExtension(extension), ex.Message);
        }

        if (entries.Any(e => e.Name.Equals("BundleConfig.pb", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith("/BundleConfig.pb", StringComparison.OrdinalIgnoreCase)))
            return Unavailable(AabMessage);

        var nested = entries.Where(e => e.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        var topLevelPayload = entries.Any(e => IsPayload(e.Name));
        if (nested.Count > 0 && topLevelPayload)
            return Reject("ambiguous", "This archive contains both nested APKs and top-level IL2CPP files. The workbench does not guess which payload to use. Renaming the archive does not select one.");

        if (nested.Count > 0)
            return PrepareNested(inputPath, workDirectory, limits, nested, extension, requestedAbi);

        if (extension.Equals(".aab", StringComparison.OrdinalIgnoreCase))
            return Unavailable(AabMessage);

        return new PreparedPackage
        {
            Kind = "apk",
            State = "ready",
            AnalysisPath = Path.GetFullPath(inputPath),
            Origins = new PackageOrigins
            {
                Kind = "apk",
                Notes = ["The archive itself is the APK. No nested package was unpacked."]
            }
        };
    }

    private static PreparedPackage PrepareNested(string inputPath, string workDirectory, ExtractionLimits limits, List<ZipName> nested, string extension, string? requestedAbi)
    {
        Directory.CreateDirectory(workDirectory);
        var membersDir = Path.Combine(workDirectory, "members");
        Directory.CreateDirectory(membersDir);
        var partsDir = Path.Combine(workDirectory, "parts");
        Directory.CreateDirectory(partsDir);

        var members = new List<PackageMemberInfo>();
        var found = new List<FoundFile>();
        long total = 0;
        try
        {
            using var stream = File.Open(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            for (var i = 0; i < nested.Count; i++)
            {
                var entry = zip.GetEntry(nested[i].Name) ?? throw new InputRejectedException("A nested APK disappeared while the archive was being read.");
                var stored = Path.Combine(membersDir, "member-" + i + ".apk");
                var written = CopyBounded(entry, stored, limits);
                total += written;
                if (total > limits.MaxTotalUncompressedBytes)
                    throw new InputRejectedException("The nested packages expand past the total extraction limit.");
                members.Add(new PackageMemberInfo
                {
                    EntryName = entry.FullName,
                    Sha256 = Hashing.Sha256File(stored),
                    SizeBytes = written
                });
                found.AddRange(CollectInner(stored, entry.FullName, partsDir, limits, i));
            }
        }
        catch (InputRejectedException ex)
        {
            return Reject(KindFromExtension(extension), ex.Message);
        }
        catch (InvalidDataException)
        {
            return Reject(KindFromExtension(extension), "A nested APK could not be read as a zip archive.");
        }

        var kind = KindFromExtension(extension);
        var metadata = found.Where(f => f.Role == "global-metadata").ToList();
        if (metadata.Count == 0)
            return Reject(kind, "No global-metadata.dat was found in the nested packages: " + Names(members) + ".");

        var metadataChoice = ChooseOne(metadata, "global-metadata.dat");
        if (metadataChoice.Error is not null)
            return Reject(kind, metadataChoice.Error);

        var natives = found.Where(f => f.Role == "libil2cpp").ToList();
        if (natives.Count == 0)
            return Reject(kind, "No libil2cpp.so was found in the nested packages: " + Names(members) + ".");

        var abi = string.IsNullOrWhiteSpace(requestedAbi) ? "arm64-v8a" : requestedAbi.Trim();
        var abiMatches = natives.Where(f => string.Equals(f.Abi, abi, StringComparison.OrdinalIgnoreCase)).ToList();
        if (abiMatches.Count == 0)
        {
            var seen = string.Join(", ", natives.Select(f => f.Abi).Distinct(StringComparer.OrdinalIgnoreCase));
            return Reject(kind, "No " + abi + " libil2cpp.so was found. ABIs present: " + seen + ". Nested packages: " + Names(members) + ".");
        }

        var nativeChoice = ChooseOne(abiMatches, abi + " libil2cpp.so");
        if (nativeChoice.Error is not null)
            return Reject(kind, nativeChoice.Error);

        var notes = new List<string>();
        if (metadata.Count > 1)
            notes.Add("global-metadata.dat was identical in " + metadata.Count + " nested packages. One copy was used.");
        if (abiMatches.Count > 1)
            notes.Add(abi + " libil2cpp.so was identical in " + abiMatches.Count + " nested packages. One copy was used.");

        var chosen = new List<(FoundFile File, string Role, string NormalizedPath)>
        {
            (metadataChoice.File!, "global-metadata", "assets/bin/Data/Managed/Metadata/global-metadata.dat"),
            (nativeChoice.File!, "libil2cpp", "lib/" + abi + "/libil2cpp.so")
        };

        foreach (var role in new[] { "globalgamemanagers", "data.unity3d" })
        {
            var group = found.Where(f => f.Role == role).ToList();
            if (group.Count == 0)
                continue;
            var choice = ChooseOne(group, role);
            if (choice.Error is not null)
                return Reject(kind, choice.Error);
            var normalized = role == "globalgamemanagers" ? "assets/bin/Data/globalgamemanagers" : "assets/bin/Data/data.unity3d";
            chosen.Add((choice.File!, role, normalized));
            if (group.Count > 1)
                notes.Add(role + " was identical in " + group.Count + " nested packages. One copy was used.");
        }

        var files = chosen.Select(item => ToOrigin(item.File, item.Role, item.NormalizedPath)).ToList();
        var normalizedApk = Path.Combine(workDirectory, "normalized.apk");
        if (File.Exists(normalizedApk))
            File.Delete(normalizedApk);
        using (var archive = ZipFile.Open(normalizedApk, ZipArchiveMode.Create))
        {
            foreach (var item in chosen)
            {
                var entry = archive.CreateEntry(item.NormalizedPath, CompressionLevel.NoCompression);
                using var input = File.OpenRead(item.File.TempPath);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }

        var origins = new PackageOrigins { Kind = kind, Members = members, Files = files, Notes = notes };
        File.WriteAllText(Path.Combine(workDirectory, "origins.json"), JsonSerializer.Serialize(origins, JsonDefaults.Options));
        return new PreparedPackage
        {
            Kind = kind,
            State = "ready",
            AnalysisPath = normalizedApk,
            Origins = origins
        };
    }

    private static PackageOriginFile ToOrigin(FoundFile file, string role, string normalized) => new()
    {
        Role = role,
        NormalizedPath = normalized,
        Origin = file.MemberEntry + "!" + file.InnerPath,
        Sha256 = file.Sha256,
        SizeBytes = file.Size
    };

    private static (FoundFile? File, string? Error) ChooseOne(List<FoundFile> files, string label)
    {
        var hashes = files.Select(f => f.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hashes.Count > 1)
        {
            var places = string.Join("; ", files.Select(f => f.MemberEntry + "!" + f.InnerPath).Distinct());
            return (null, "Conflicting " + label + " files were found and neither was chosen: " + places + ".");
        }
        return (files[0], null);
    }

    private static List<FoundFile> CollectInner(string apkPath, string memberEntry, string partsDir, ExtractionLimits limits, int memberIndex)
    {
        var found = new List<FoundFile>();
        using var stream = File.Open(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > limits.MaxArchiveEntries)
            throw new InputRejectedException(memberEntry + " has " + zip.Entries.Count + " entries, above the limit of " + limits.MaxArchiveEntries + ".");

        long total = 0;
        var payloadIndex = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            if (!ApkArchive.IsSafeName(entry.FullName))
                throw new InputRejectedException(memberEntry + " contains an unsafe path '" + entry.FullName + "'. It was not extracted.");
            if (entry.Length > limits.MaxEntryUncompressedBytes)
                throw new InputRejectedException(memberEntry + "!" + entry.FullName + " is " + entry.Length + " bytes, above the per-file limit.");
            if (entry.CompressedLength >= limits.CompressionRatioMinCompressedBytes && entry.CompressedLength > 0 &&
                (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                throw new InputRejectedException(memberEntry + "!" + entry.FullName + " expands more than " + limits.MaxCompressionRatio + " times. Extraction stopped.");
            total += entry.Length;
            if (total > limits.MaxTotalUncompressedBytes)
                throw new InputRejectedException(memberEntry + " expands past the total extraction limit.");
            if (entry.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new InputRejectedException(memberEntry + " contains another APK (" + entry.FullName + "). Nested packages inside a split are not opened.");

            var role = RoleOf(entry.FullName);
            if (role is null)
                continue;
            var stored = Path.Combine(partsDir, memberIndex + "-" + payloadIndex + ".bin");
            payloadIndex++;
            var size = CopyBounded(entry, stored, limits);
            found.Add(new FoundFile
            {
                MemberEntry = memberEntry,
                InnerPath = entry.FullName.Replace('\\', '/'),
                Role = role,
                Abi = AbiOf(entry.FullName),
                TempPath = stored,
                Sha256 = Hashing.Sha256File(stored),
                Size = size
            });
        }
        return found;
    }

    private static long CopyBounded(ZipArchiveEntry entry, string destination, ExtractionLimits limits)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = entry.Open();
        using var output = File.Create(destination);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > entry.Length || written > limits.MaxEntryUncompressedBytes)
                throw new InputRejectedException("'" + entry.FullName + "' expanded past its declared size or the entry limit. Extraction stopped.");
            output.Write(buffer, 0, read);
        }
        return written;
    }

    private static List<ZipName> ListEntries(string path, ExtractionLimits limits)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > limits.MaxArchiveEntries)
            throw new InputRejectedException("The archive has " + zip.Entries.Count + " entries, above the limit of " + limits.MaxArchiveEntries + ".");
        var list = new List<ZipName>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!ApkArchive.IsSafeName(entry.FullName) && !string.IsNullOrEmpty(entry.Name))
                throw new InputRejectedException("The entry '" + entry.FullName + "' is absolute or contains a parent-directory segment.");
            if (entry.Length > limits.MaxEntryUncompressedBytes)
                throw new InputRejectedException("'" + entry.FullName + "' is " + entry.Length + " bytes, above the per-file limit.");
            if (entry.CompressedLength >= limits.CompressionRatioMinCompressedBytes && entry.CompressedLength > 0 &&
                (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                throw new InputRejectedException("'" + entry.FullName + "' expands more than " + limits.MaxCompressionRatio + " times. Extraction stopped.");
            total += entry.Length;
            if (total > limits.MaxTotalUncompressedBytes)
                throw new InputRejectedException("The archive's declared uncompressed size exceeds the total extraction limit.");
            if (!string.IsNullOrEmpty(entry.Name))
                list.Add(new ZipName(entry.FullName.Replace('\\', '/')));
        }
        return list;
    }

    private static string? RoleOf(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/'));
        if (file.Equals("libil2cpp.so", StringComparison.OrdinalIgnoreCase))
            return "libil2cpp";
        if (file.Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase))
            return "global-metadata";
        if (file.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase))
            return "globalgamemanagers";
        if (file.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase))
            return "data.unity3d";
        return null;
    }

    private static string AbiOf(string name)
    {
        var parts = name.Replace('\\', '/').Split('/');
        var lib = Array.FindIndex(parts, p => p.Equals("lib", StringComparison.OrdinalIgnoreCase));
        if (lib >= 0 && lib + 1 < parts.Length)
            return parts[lib + 1];
        return "unknown";
    }

    private static bool IsPayload(string name)
    {
        var file = Path.GetFileName(name);
        return file.Equals("libil2cpp.so", StringComparison.OrdinalIgnoreCase) || file.Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase);
    }

    private static string KindFromExtension(string extension)
    {
        var value = extension.TrimStart('.').ToLowerInvariant();
        return value is "xapk" or "apks" or "apkm" ? value : "nested";
    }

    private static string Names(IEnumerable<PackageMemberInfo> members) =>
        string.Join(", ", members.Select(m => m.EntryName));

    private static PreparedPackage Reject(string kind, string message) => new()
    {
        Kind = kind,
        State = "rejected",
        Message = message
    };

    private static PreparedPackage Unavailable(string message) => new()
    {
        Kind = "aab",
        State = "unavailable",
        Message = message
    };

    private static byte[] ReadHeader(string path)
    {
        var buffer = new byte[8];
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.Read(buffer, 0, buffer.Length);
        return buffer.AsSpan(0, read).ToArray();
    }

    private static bool IsZip(byte[] header) => header.Length >= 2 && header[0] == (byte)'P' && header[1] == (byte)'K';

    private static bool IsElf(byte[] header) => header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F';

    private static bool IsPe(byte[] header) => header.Length >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';

    private static bool IsWasm(byte[] header) => header.Length >= 4 && header[0] == 0x00 && header[1] == (byte)'a' && header[2] == (byte)'s' && header[3] == (byte)'m';

    private sealed record ZipName(string Name);

    private sealed class FoundFile
    {
        public required string MemberEntry { get; init; }
        public required string InnerPath { get; init; }
        public required string Role { get; init; }
        public required string Abi { get; init; }
        public required string TempPath { get; init; }
        public required string Sha256 { get; init; }
        public long Size { get; init; }
    }
}

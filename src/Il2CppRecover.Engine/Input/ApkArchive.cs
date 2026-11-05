using System.IO.Compression;
using System.Text;
using Il2CppRecover.Binary;

namespace Il2CppRecover.Input;

public sealed class InputRejectedException : Exception
{
    public InputRejectedException(string message) : base(message)
    {
    }
}

public static class ApkArchive
{
    private static readonly string[] SplitExtensions = [".xapk", ".apks", ".apkm"];

    public static InventoryResult Inspect(string apkPath, string outputDirectory, ExtractionLimits limits, string? requestedAbi, string? suppliedUnityVersion)
    {
        if (string.IsNullOrWhiteSpace(apkPath) || !File.Exists(apkPath))
            throw new InputRejectedException("The input file does not exist.");

        var info = new FileInfo(apkPath);
        if (info.Length > limits.MaxInputBytes)
            throw new InputRejectedException($"The input is {info.Length} bytes, above the limit of {limits.MaxInputBytes}.");

        var extension = Path.GetExtension(apkPath);
        var diagnostics = new List<string>();
        if (SplitExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add($"'{extension}' is a split or bundle package. This milestone reads a single APK. Unpack the base APK and the matching ABI split locally before importing.");
        }

        var inputHash = Hashing.Sha256File(apkPath);
        Directory.CreateDirectory(outputDirectory);
        var extractRoot = Path.Combine(outputDirectory, "extracted");
        Directory.CreateDirectory(extractRoot);

        List<ArchiveEntryInfo> entries;
        try
        {
            entries = ReadEntries(apkPath, limits, diagnostics);
        }
        catch (InvalidDataException ex)
        {
            throw new InputRejectedException("The input is not a readable ZIP/APK archive: " + ex.Message);
        }

        if (entries.Any(e => e.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add("The archive contains nested APK entries. Split APKs, XAPKs and APKS are not extracted in this milestone.");

        var fatal = diagnostics.Count > 0;
        var extracted = fatal ? [] : ExtractRelevant(apkPath, extractRoot, entries, limits, diagnostics);
        var metadataEntry = extracted.FirstOrDefault(f => f.Role == "global-metadata");
        var ggm = extracted.FirstOrDefault(f => f.Role == "globalgamemanagers");
        var dataUnity = extracted.FirstOrDefault(f => f.Role == "data.unity3d");
        var natives = new List<NativeBinaryInspection>();
        foreach (var native in extracted.Where(f => f.Role == "libil2cpp"))
        {
            var bytes = File.ReadAllBytes(native.OutputPath);
            natives.Add(InspectNative(native, bytes));
        }

        var metadata = metadataEntry is null ? null : InspectMetadata(metadataEntry);
        var backend = Classify(entries, metadata, natives);
        var unity = new UnityVersionDetection();
        if (!fatal)
            unity = UnityVersionLocator.Detect(extracted, suppliedUnityVersion, diagnostics);

        NativeBinaryInspection? selected = null;
        string? selectedAbi = null;
        if (!fatal && natives.Count > 0)
        {
            selected = SelectBinary(natives, requestedAbi, diagnostics);
            selectedAbi = selected?.Abi;
        }

        if (backend == ScriptingBackend.Mono)
            diagnostics.Add("Managed assemblies were found and libil2cpp.so was not. This is a Mono build. IL2CPP metadata and native recovery do not apply.");
        if (backend == ScriptingBackend.Unknown && !fatal)
            diagnostics.Add("Neither libil2cpp.so nor a Managed assembly directory was found.");
        if (metadata is { SuspectedProtection: true })
            diagnostics.Add(metadata.Note ?? "Metadata did not have the IL2CPP magic number.");
        if (selected is { Architecture: not NativeArchitecture.Arm64 } && selected.Elf)
            diagnostics.Add($"Selected ABI '{selected.Abi}' is {selected.Architecture}. ARM64 little-endian is the only architecture with a recovery lifter in this milestone.");

        var succeeded = !fatal && backend == ScriptingBackend.Il2Cpp && metadata is { MagicValid: true } && selected is { Architecture: NativeArchitecture.Arm64, LittleEndian: true, Is64Bit: true };
        return new InventoryResult
        {
            ToolName = ToolInfo.Name,
            ToolVersion = ToolInfo.Version,
            InputPath = Path.GetFullPath(apkPath),
            InputSha256 = inputHash,
            InputSizeBytes = info.Length,
            InputKind = fatal && SplitExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? extension.TrimStart('.') : "apk",
            ScriptingBackend = backend,
            UnityVersion = unity,
            Metadata = metadata,
            NativeBinaries = natives,
            SelectedAbi = selectedAbi,
            SelectedBinary = selected,
            ExtractedFiles = extracted,
            Entries = entries,
            Diagnostics = diagnostics,
            InputPreserved = true,
            Succeeded = succeeded
        };
    }

    private static List<ArchiveEntryInfo> ReadEntries(string apkPath, ExtractionLimits limits, List<string> diagnostics)
    {
        using var stream = File.Open(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > limits.MaxArchiveEntries)
            throw new InputRejectedException($"The archive has {zip.Entries.Count} entries, above the limit of {limits.MaxArchiveEntries}.");

        var entries = new List<ArchiveEntryInfo>(zip.Entries.Count);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string? rejection = null;
            if (!IsSafeName(entry.FullName))
                rejection = "The entry name is absolute or contains a parent-directory segment.";
            else if (entry.Length > limits.MaxEntryUncompressedBytes)
                rejection = $"Uncompressed size {entry.Length} exceeds the per-entry limit.";
            else if (entry.CompressedLength >= limits.CompressionRatioMinCompressedBytes && entry.CompressedLength > 0 &&
                     (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                rejection = $"Compression ratio {(double)entry.Length / entry.CompressedLength:F1} exceeds the limit of {limits.MaxCompressionRatio}.";

            if (rejection is null)
            {
                total += entry.Length;
                if (total > limits.MaxTotalUncompressedBytes)
                    throw new InputRejectedException("The archive's declared uncompressed size exceeds the total extraction limit.");
            }
            else
            {
                diagnostics.Add(entry.FullName + ": " + rejection);
            }

            entries.Add(new ArchiveEntryInfo
            {
                Name = entry.FullName,
                CompressedBytes = entry.CompressedLength,
                UncompressedBytes = entry.Length,
                Rejected = rejection is not null,
                Rejection = rejection
            });
        }
        return entries;
    }

    private static List<ExtractedFile> ExtractRelevant(string apkPath, string extractRoot, List<ArchiveEntryInfo> entries, ExtractionLimits limits, List<string> diagnostics)
    {
        var wanted = entries.Where(e => !e.Rejected && IsRelevant(e.Name)).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var extracted = new List<ExtractedFile>();
        if (wanted.Count == 0)
            return extracted;

        using var stream = File.Open(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (!wanted.Contains(entry.FullName))
                continue;
            var relative = entry.FullName.Replace('\\', '/');
            var destination = Path.GetFullPath(Path.Combine(extractRoot, relative));
            var root = Path.GetFullPath(extractRoot) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(root, StringComparison.Ordinal))
            {
                diagnostics.Add(entry.FullName + ": extraction path escaped the output directory and was skipped.");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            long written;
            using (var input = entry.Open())
            using (var output = File.Create(destination))
            {
                var buffer = new byte[81920];
                written = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > entry.Length || written > limits.MaxEntryUncompressedBytes)
                        throw new InputRejectedException($"Entry '{entry.FullName}' expanded past its declared size or the entry limit. Extraction stopped.");
                    output.Write(buffer, 0, read);
                }
            }
            extracted.Add(new ExtractedFile
            {
                ArchivePath = entry.FullName,
                OutputPath = destination,
                Sha256 = Hashing.Sha256File(destination),
                SizeBytes = written,
                Role = RoleFor(entry.FullName)
            });
        }
        return extracted;
    }

    public static bool IsSafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
            return false;
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            return false;
        var segments = normalized.Split('/');
        return segments.All(s => s.Length > 0 && s is not (".." or "."));
    }

    private static bool IsRelevant(string name)
    {
        var normalized = name.Replace('\\', '/');
        var file = Path.GetFileName(normalized);
        return file.Equals("libil2cpp.so", StringComparison.OrdinalIgnoreCase)
            || file.Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase)
            || file.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase)
            || file.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase);
    }

    private static string RoleFor(string name)
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
        return "other";
    }

    private static MetadataInspection InspectMetadata(ExtractedFile file)
    {
        var bytes = File.ReadAllBytes(file.OutputPath);
        uint? magic = bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0) : null;
        int? version = bytes.Length >= 8 ? BitConverter.ToInt32(bytes, 4) : null;
        var valid = magic == 0xFAB11BAF;
        return new MetadataInspection
        {
            ArchivePath = file.ArchivePath,
            Sha256 = file.Sha256,
            SizeBytes = file.SizeBytes,
            MagicValid = valid,
            Magic = magic,
            HeaderVersion = valid ? version : null,
            SuspectedProtection = !valid,
            Note = valid
                ? "Metadata magic 0xFAB11BAF was read. The header version is the integer stored at offset 4 and was not rewritten."
                : "global-metadata.dat does not start with magic 0xFAB11BAF. Encrypted, packed, or substituted metadata is not supported, and no version was inferred."
        };
    }

    private static NativeBinaryInspection InspectNative(ExtractedFile file, byte[] bytes)
    {
        var abi = AbiFromPath(file.ArchivePath);
        if (!ElfImage.TryParse(bytes, out var elf, out var error))
        {
            return new NativeBinaryInspection
            {
                ArchivePath = file.ArchivePath,
                Sha256 = file.Sha256,
                SizeBytes = file.SizeBytes,
                Abi = abi,
                Elf = elf is { LittleEndian: false },
                Is64Bit = elf?.Is64Bit ?? false,
                LittleEndian = elf?.LittleEndian ?? false,
                Machine = elf?.Machine,
                Architecture = elf?.Architecture ?? NativeArchitecture.Unknown,
                Note = error
            };
        }
        return new NativeBinaryInspection
        {
            ArchivePath = file.ArchivePath,
            Sha256 = file.Sha256,
            SizeBytes = file.SizeBytes,
            Abi = abi,
            Elf = true,
            Is64Bit = elf!.Is64Bit,
            LittleEndian = elf.LittleEndian,
            Machine = elf.Machine,
            Architecture = elf.Architecture,
            FileType = elf.Note,
            Note = elf.Note
        };
    }

    public static string? AbiFromPath(string archivePath)
    {
        var parts = archivePath.Replace('\\', '/').Split('/');
        var lib = Array.FindIndex(parts, p => p.Equals("lib", StringComparison.OrdinalIgnoreCase));
        if (lib >= 0 && lib + 1 < parts.Length)
            return parts[lib + 1];
        return null;
    }

    private static ScriptingBackend Classify(List<ArchiveEntryInfo> entries, MetadataInspection? metadata, List<NativeBinaryInspection> natives)
    {
        var hasIl2Cpp = natives.Count > 0 || entries.Any(e => Path.GetFileName(e.Name).Equals("libil2cpp.so", StringComparison.OrdinalIgnoreCase));
        var hasMetadata = metadata is not null || entries.Any(e => Path.GetFileName(e.Name).Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase));
        var hasManaged = entries.Any(e =>
        {
            var name = e.Name.Replace('\\', '/');
            return name.Contains("/Managed/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        });
        if ((hasIl2Cpp || hasMetadata) && hasManaged)
            return ScriptingBackend.Mixed;
        if (hasIl2Cpp || hasMetadata)
            return ScriptingBackend.Il2Cpp;
        if (hasManaged)
            return ScriptingBackend.Mono;
        return ScriptingBackend.Unknown;
    }

    private static NativeBinaryInspection? SelectBinary(List<NativeBinaryInspection> natives, string? requestedAbi, List<string> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(requestedAbi))
        {
            var match = natives.FirstOrDefault(n => string.Equals(n.Abi, requestedAbi, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                diagnostics.Add($"Requested ABI '{requestedAbi}' was not present. Available: {string.Join(", ", natives.Select(n => n.Abi ?? "(none)"))}.");
            return match;
        }
        var arm64 = natives.FirstOrDefault(n => string.Equals(n.Abi, "arm64-v8a", StringComparison.OrdinalIgnoreCase));
        if (arm64 is not null)
            return arm64;
        if (natives.Count == 1)
            return natives[0];
        diagnostics.Add("Multiple native binaries were found and none is arm64-v8a. Pass an explicit ABI. None was selected.");
        return null;
    }
}

public static class UnityVersionLocator
{
    private static readonly System.Text.RegularExpressions.Regex VersionPattern = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+[abcfxp][0-9]+$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static UnityVersionDetection Detect(IReadOnlyList<ExtractedFile> files, string? supplied, List<string> diagnostics)
    {
        var observed = new List<string>();
        string? detected = null;
        string? source = null;

        foreach (var file in files)
        {
            if (file.Role == "globalgamemanagers")
                ReadStructured(file, 0x14, "globalgamemanagers+0x14", ref detected, ref source, observed);
            if (file.Role == "globalgamemanagers" && detected is null)
                ReadStructured(file, 0x30, "globalgamemanagers+0x30", ref detected, ref source, observed);
            if (file.Role == "data.unity3d")
                ReadStructured(file, 0x12, "data.unity3d+0x12", ref detected, ref source, observed);
        }

        if (detected is null)
        {
            var scanned = Scan(files);
            observed.AddRange(scanned.Select(s => s.Version + " @ " + s.Source));
            var distinct = scanned.Select(s => s.Version).Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count == 1)
            {
                detected = distinct[0];
                source = scanned.First(s => s.Version == detected).Source;
            }
            else if (distinct.Count > 1)
            {
                diagnostics.Add("More than one Unity version string was found (" + string.Join(", ", distinct) + "). No version was chosen.");
                return new UnityVersionDetection { Ambiguous = true, ObservedStrings = observed };
            }
        }

        if (!string.IsNullOrWhiteSpace(supplied))
        {
            if (!VersionPattern.IsMatch(supplied))
            {
                diagnostics.Add("The supplied Unity version '" + supplied + "' does not match Unity's version shape. It was ignored.");
            }
            else if (detected is not null && !string.Equals(detected, supplied, StringComparison.Ordinal))
            {
                diagnostics.Add($"The supplied Unity version '{supplied}' does not match the version string '{detected}' read from {source}. The file's string was kept.");
            }
            else if (detected is null)
            {
                detected = supplied;
                source = "user-supplied";
                observed.Add(supplied + " @ user-supplied");
            }
        }

        return new UnityVersionDetection
        {
            Version = detected,
            Source = source,
            Ambiguous = false,
            ObservedStrings = observed
        };
    }

    private static void ReadStructured(ExtractedFile file, int offset, string label, ref string? detected, ref string? source, List<string> observed)
    {
        var bytes = File.ReadAllBytes(file.OutputPath);
        var text = ReadCString(bytes, offset);
        if (text is null || !VersionPattern.IsMatch(text))
            return;
        observed.Add(text + " @ " + label);
        if (detected is null)
        {
            detected = text;
            source = label;
        }
        else if (!string.Equals(detected, text, StringComparison.Ordinal))
        {
            detected = null;
            source = null;
        }
    }

    private static List<(string Version, string Source)> Scan(IReadOnlyList<ExtractedFile> files)
    {
        var found = new List<(string, string)>();
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file.OutputPath);
            var limit = Math.Min(bytes.Length, 2 * 1024 * 1024);
            var text = Encoding.ASCII.GetString(bytes, 0, limit);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"[0-9]+\.[0-9]+\.[0-9]+[abcfxp][0-9]+"))
            {
                if (VersionPattern.IsMatch(match.Value))
                    found.Add((match.Value, "scan:" + file.Role));
            }
        }
        return found;
    }

    private static string? ReadCString(byte[] bytes, int offset)
    {
        if (offset < 0 || offset >= bytes.Length)
            return null;
        var end = offset;
        while (end < bytes.Length && bytes[end] != 0 && end - offset < 64)
            end++;
        if (end == offset || end >= bytes.Length || bytes[end] != 0)
            return null;
        return Encoding.ASCII.GetString(bytes, offset, end - offset);
    }
}

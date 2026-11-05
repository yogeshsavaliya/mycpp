using Il2CppRecover.Binary;
using Il2CppRecover.EngineAdapter;
using Il2CppRecover.Input;
using Il2CppRecover.Metadata;
using Il2CppRecover.Recovery;

namespace Il2CppRecover.Analysis;

public sealed class AnalysisOptions
{
    public ExtractionLimits Limits { get; init; } = new();
    public string? RequestedAbi { get; init; }
    public string? SuppliedUnityVersion { get; init; }
    public bool TryNativeLoad { get; init; } = true;
    public bool EmitPlaceholders { get; init; } = true;
}

public static class AnalysisPipeline
{
    public static AnalysisOutput Analyze(string apkPath, string outputDirectory, AnalysisOptions options, IProgress<ProgressEvent>? progress, CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        void Log(string stage, string? detail = null)
        {
            logs.Add(DateTimeOffset.UtcNow.ToString("O") + " " + stage + (detail is null ? "" : " — " + detail));
            progress?.Report(new ProgressEvent { Stage = stage, Detail = detail });
        }

        cancellationToken.ThrowIfCancellationRequested();
        Log("Hash and inventory");
        var inventory = ApkArchive.Inspect(apkPath, outputDirectory, options.Limits, options.RequestedAbi, options.SuppliedUnityVersion);
        Log("Inventory finished", "backend=" + inventory.ScriptingBackend + ", succeeded=" + inventory.Succeeded);

        cancellationToken.ThrowIfCancellationRequested();
        MetadataCatalog? catalog = null;
        var metadataFile = inventory.ExtractedFiles.FirstOrDefault(f => f.Role == "global-metadata");
        if (metadataFile is not null && inventory.Metadata is { MagicValid: true })
        {
            Log("Reading metadata tables");
            catalog = MetadataCatalogReader.Read(File.ReadAllBytes(metadataFile.OutputPath), inventory.UnityVersion.Version, inventory.UnityVersion.Source);
            Log("Metadata tables finished", catalog.Images.Sum(i => i.Types.Sum(t => t.Methods.Count)) + " methods, layout=" + (catalog.LibCpp2IlLayoutVersion?.ToString() ?? "not parsed"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        EngineLoadResult? engine = null;
        byte[]? binaryBytes = null;
        ElfImage? elf = null;
        var binaryFile = inventory.SelectedBinary is null
            ? null
            : inventory.ExtractedFiles.FirstOrDefault(f => f.ArchivePath == inventory.SelectedBinary.ArchivePath);
        if (options.TryNativeLoad && binaryFile is not null && metadataFile is not null && inventory.Metadata is { MagicValid: true })
        {
            binaryBytes = File.ReadAllBytes(binaryFile.OutputPath);
            ElfImage.TryParse(binaryBytes, out elf, out _);
            if (inventory.UnityVersion.Version is null)
            {
                engine = new EngineLoadResult
                {
                    Loaded = false,
                    Diagnostic = "Code registration was not queried. No Unity version string was read from the input, and a version was not invented to drive the native loader."
                };
                Log("Native loader skipped", engine.Diagnostic);
            }
            else if (inventory.SelectedBinary?.Architecture != NativeArchitecture.Arm64)
            {
                engine = new EngineLoadResult
                {
                    Loaded = false,
                    Diagnostic = "Native loader was not run because the selected binary is not little-endian ARM64."
                };
                Log("Native loader skipped", engine.Diagnostic);
            }
            else
            {
                Log("Loading binary with pinned LibCpp2IL");
                engine = Cpp2IlSession.TryLoad(binaryBytes, File.ReadAllBytes(metadataFile.OutputPath), inventory.UnityVersion.Version);
                Log(engine.Loaded ? "LibCpp2IL loaded the binary" : "LibCpp2IL did not load the binary", engine.Diagnostic);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var methods = BuildMethods(catalog, engine, binaryBytes, elf, options.EmitPlaceholders, cancellationToken, progress);
        var report = RecoveryReportBuilder.Build(methods);
        Log("Writing output");
        OutputWriter.Write(outputDirectory, inventory, catalog, methods, report, logs, engine?.Diagnostic);
        return new AnalysisOutput
        {
            Inventory = inventory,
            Catalog = catalog,
            Methods = methods,
            Report = report,
            OutputDirectory = Path.GetFullPath(outputDirectory),
            Logs = logs,
            EngineLoadDiagnostic = engine?.Diagnostic
        };
    }

    private static List<MethodRecord> BuildMethods(MetadataCatalog? catalog, EngineLoadResult? engine, byte[]? binary, ElfImage? elf, bool placeholders, CancellationToken cancellationToken, IProgress<ProgressEvent>? progress)
    {
        var records = new List<MethodRecord>();
        if (catalog is null)
            return records;

        var nativeByIndex = (engine?.Methods ?? []).Where(m => m.MetadataIndex >= 0).ToDictionary(m => m.MetadataIndex);
        var orderedAddresses = nativeByIndex.Values.Select(m => m.VirtualAddress).Where(a => a != 0).Distinct().Order().ToList();
        var total = catalog.Images.Sum(i => i.Types.Sum(t => t.Methods.Count));
        var done = 0;
        foreach (var image in catalog.Images)
        {
            foreach (var type in image.Types)
            {
                foreach (var method in type.Methods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    done++;
                    if (done % 50 == 0)
                        progress?.Report(new ProgressEvent { Stage = "Methods", Completed = done, Total = total });
                    records.Add(BuildOne(image, type, method, nativeByIndex, orderedAddresses, binary, elf, placeholders));
                }
            }
        }

        foreach (var group in records.Where(r => !string.IsNullOrEmpty(r.NativeVirtualAddress)).GroupBy(r => r.NativeVirtualAddress))
        {
            if (group.Count() < 2)
                continue;
            foreach (var record in group)
            {
                record.SharedImplementationCount = group.Count();
                if (record.MethodKind == MethodKind.Concrete)
                    record.MethodKind = MethodKind.SharedNative;
            }
        }
        return records;
    }

    private static MethodRecord BuildOne(CatalogImage image, CatalogType type, CatalogMethod method, Dictionary<int, EngineMethod> nativeByIndex, List<ulong> orderedAddresses, byte[]? binary, ElfImage? elf, bool placeholders)
    {
        var kind = method.IsAbstract ? MethodKind.Abstract : method.IsExternal ? MethodKind.External : method.IsGeneric ? MethodKind.Generic : MethodKind.Concrete;
        var diagnostics = new List<string>();
        nativeByIndex.TryGetValue(method.MetadataIndex, out var resolvedNative);
        var returnTypeName = ResolveDeclaredTypes(method, resolvedNative, diagnostics, out var parameterTypeNames);
        var signature = (method.IsStatic ? "static " : "") + returnTypeName + " " + type.Namespace + (type.Namespace.Length == 0 ? "" : ".") + type.Name + "::" + method.Name + "(" + string.Join(", ", method.Parameters.Select((p, i) => parameterTypeNames[i] + " " + p.Name)) + ")";
        string? address = null;
        string? fileOffset = null;
        var instructions = new List<NativeInstruction>();
        string? csharp = null;
        var placeholder = false;
        var status = MethodRecoveryStatus.MetadataOnly;
        var provenance = new List<ProvenanceLink>();
        string? candidate = null;
        var boundary = BoundaryEvidence.NotApplicable;

        if (kind is MethodKind.Abstract or MethodKind.External)
            diagnostics.Add(kind + " method. No native body is expected, and none was invented.");
        else if (!nativeByIndex.TryGetValue(method.MetadataIndex, out var native) || native.VirtualAddress == 0)
            diagnostics.Add("No native virtual address was located. Metadata names are not recovered method logic.");
        else if (binary is null || elf is null || !elf.TryMap(native.VirtualAddress, out var offset))
        {
            address = "0x" + native.VirtualAddress.ToString("X");
            diagnostics.Add("A virtual address was reported but it does not map into a loaded ELF segment.");
            status = MethodRecoveryStatus.Failed;
        }
        else
        {
            address = "0x" + native.VirtualAddress.ToString("X");
            fileOffset = "0x" + offset.ToString("X");
            var available = (int)Math.Min(int.MaxValue, binary.Length - offset);
            var length = SliceLength(native.VirtualAddress, orderedAddresses, available);
            var code = binary.AsSpan((int)offset, length).ToArray();
            boundary = BoundaryEvidence.NextMethodAddressOnly;
            if (kind == MethodKind.Generic)
            {
                instructions.AddRange(Arm64Recoverer.Disassemble(code, native.VirtualAddress));
                status = instructions.Count > 0 ? MethodRecoveryStatus.Disassembled : MethodRecoveryStatus.Unsupported;
                diagnostics.Add("Generic methods are disassembled only. Shared generic bodies are not recovered in this milestone.");
            }
            else if (!TryContract(image, type, method, native, out var contract, out var why))
            {
                instructions.AddRange(Arm64Recoverer.Disassemble(code, native.VirtualAddress));
                status = instructions.Count > 0 ? MethodRecoveryStatus.Disassembled : MethodRecoveryStatus.MetadataOnly;
                diagnostics.Add(why);
            }
            else
            {
                var lifted = Arm64Recoverer.Recover(new RecoveryRequest
                {
                    Code = code,
                    VirtualAddress = native.VirtualAddress,
                    Contract = contract,
                    Boundary = BoundaryEvidence.NextMethodAddressOnly
                });
                instructions.AddRange(lifted.Instructions);
                diagnostics.AddRange(lifted.Diagnostics);
                status = lifted.Status;
                csharp = lifted.CSharp;
                placeholder = lifted.CSharpIsPlaceholder;
                candidate = lifted.CandidateCSharp;
                provenance.AddRange(lifted.Provenance);
            }
        }

        if (csharp is null && placeholders && kind is not (MethodKind.Abstract or MethodKind.External))
        {
            csharp = CSharpEmitter.Placeholder(status, diagnostics);
            placeholder = true;
        }

        return new MethodRecord
        {
            Assembly = image.Name,
            Namespace = type.Namespace,
            DeclaringType = type.Name,
            Name = method.Name,
            Signature = signature,
            ReturnTypeName = returnTypeName,
            ParameterNames = method.Parameters.Select(p => p.Name).ToList(),
            ParameterTypeNames = parameterTypeNames,
            MetadataToken = method.Token,
            MetadataMethodIndex = method.MetadataIndex,
            IsStatic = method.IsStatic,
            MethodKind = kind,
            NativeVirtualAddress = address,
            NativeFileOffset = fileOffset,
            BoundaryEvidence = boundary,
            Status = status,
            Diagnostics = diagnostics,
            Instructions = instructions,
            CSharp = csharp,
            CSharpIsPlaceholder = placeholder,
            CandidateCSharp = candidate,
            Provenance = provenance
        };
    }

    private static int SliceLength(ulong address, List<ulong> ordered, int available)
    {
        var index = ordered.BinarySearch(address);
        var cap = Math.Min(available, 8192);
        if (index >= 0 && index + 1 < ordered.Count)
        {
            var delta = (int)Math.Min(int.MaxValue, ordered[index + 1] - address);
            if (delta > 0)
                cap = Math.Min(cap, delta);
        }
        cap -= cap % 4;
        return Math.Max(cap, 0);
    }

    private static bool TryContract(CatalogImage image, CatalogType type, CatalogMethod method, EngineMethod native, out MethodContract contract, out string error)
    {
        contract = new MethodContract { Name = method.Name, DeclaringType = type.Name, Namespace = type.Namespace, Assembly = image.Name, IsStatic = method.IsStatic };
        error = "";
        if (!TryPrimitive(native.ReturnType, out var ret))
        {
            error = "Return type '" + native.ReturnType + "' is outside the lifter subset, so no body was invented.";
            return false;
        }
        if (native.ParameterTypes.Count != method.Parameters.Count)
        {
            error = "The native loader returned " + native.ParameterTypes.Count + " parameter types for a method with " + method.Parameters.Count + " metadata parameters.";
            return false;
        }
        var parameters = new List<ParameterContract>();
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (!TryPrimitive(native.ParameterTypes[i], out var primitive))
            {
                error = "Parameter type '" + native.ParameterTypes[i] + "' is outside the lifter subset.";
                return false;
            }
            parameters.Add(new ParameterContract { Name = method.Parameters[i].Name, Type = primitive });
        }
        contract = new MethodContract
        {
            Name = method.Name,
            DeclaringType = type.Name,
            Namespace = type.Namespace,
            Assembly = image.Name,
            IsStatic = method.IsStatic,
            IsGeneric = method.IsGeneric,
            ReturnType = ret,
            Parameters = parameters
        };
        return true;
    }

    public static string ResolveDeclaredTypes(CatalogMethod method, EngineMethod? native, IList<string> diagnostics, out IReadOnlyList<string> parameterTypes)
    {
        var returnType = string.IsNullOrWhiteSpace(method.ReturnType) ? "unresolved" : method.ReturnType;
        var parameters = method.Parameters.Select(p => string.IsNullOrWhiteSpace(p.Type) ? "unresolved" : p.Type).ToList();
        if (native is not null)
        {
            if (!string.IsNullOrWhiteSpace(native.ReturnType))
                returnType = native.ReturnType;
            if (native.ParameterTypes.Count == method.Parameters.Count)
                parameters = native.ParameterTypes.Select(type => string.IsNullOrWhiteSpace(type) ? "unresolved" : type).ToList();
            else
                diagnostics.Add("The native loader returned " + native.ParameterTypes.Count + " parameter types for " + method.Parameters.Count + " metadata parameters. Parameter types were left unresolved and were not replaced with int.");
        }

        parameterTypes = parameters;
        return returnType;
    }

    public static bool TryPrimitive(string text, out PrimitiveKind kind)
    {
        var trimmed = text.Replace("System.", "", StringComparison.Ordinal).Trim();
        kind = trimmed switch
        {
            "Void" => PrimitiveKind.Void,
            "Boolean" => PrimitiveKind.Boolean,
            "Int32" => PrimitiveKind.Int32,
            "UInt32" => PrimitiveKind.UInt32,
            "Int64" => PrimitiveKind.Int64,
            "UInt64" => PrimitiveKind.UInt64,
            "Single" => PrimitiveKind.Single,
            "Double" => PrimitiveKind.Double,
            "Byte" or "SByte" or "Int16" or "UInt16" or "Char" => PrimitiveKind.Int32,
            _ => PrimitiveKind.Reference
        };
        if (trimmed is "Byte" or "SByte" or "Int16" or "UInt16" or "Char")
            return false;
        return trimmed is "Void" or "Boolean" or "Int32" or "UInt32" or "Int64" or "UInt64" or "Single" or "Double";
    }
}

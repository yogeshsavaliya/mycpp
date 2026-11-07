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
    /// <summary>When true, framework assemblies keep their metadata and call targets, and their bodies are left unanalyzed.</summary>
    public bool GameplayFirst { get; init; }
    public AnalysisBudget Budget { get; init; } = AnalysisBudget.Default;
}

public static class AnalysisPipeline
{
    public static AnalysisOutput Analyze(string apkPath, string outputDirectory, AnalysisOptions options, IProgress<ProgressEvent>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        using var trace = new AnalysisTrace(Path.Combine(outputDirectory, "analysis-trace.jsonl"));
        var logs = new List<string>();
        var pace = new Pace();
        void Log(string stage, string? detail = null, string kind = "stage", int? done = null, int? total = null)
        {
            logs.Add(DateTimeOffset.UtcNow.ToString("O") + " " + stage + (detail is null ? "" : " — " + detail));
            progress?.Report(new ProgressEvent { Stage = stage, Detail = detail, Kind = kind, Completed = done, Total = total });
            trace.Write(kind.Length == 0 ? "stage" : kind, new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["detail"] = detail,
                ["completed"] = done,
                ["total"] = total
            });
        }

        trace.Write("identity", new Dictionary<string, object?>
        {
            ["build"] = ToolInfo.BuildIdentity,
            ["pid"] = Environment.ProcessId,
            ["abi"] = options.RequestedAbi,
            ["bodyScope"] = options.GameplayFirst ? "gameplay-first" : "full"
        });
        cancellationToken.ThrowIfCancellationRequested();
        Log("Hash and inventory");
        var inventory = ApkArchive.Inspect(apkPath, outputDirectory, options.Limits, options.RequestedAbi, options.SuppliedUnityVersion);
        Log("Inventory finished", "backend=" + inventory.ScriptingBackend + ", succeeded=" + inventory.Succeeded);
        var identity = "build=" + ToolInfo.BuildIdentity + " pid=" + Environment.ProcessId + " sha256=" + inventory.InputSha256 + " abi=" + (inventory.SelectedAbi ?? options.RequestedAbi ?? "unselected");
        Log("Identity", identity, "identity");
        trace.Write("identity", new Dictionary<string, object?>
        {
            ["build"] = ToolInfo.BuildIdentity,
            ["pid"] = Environment.ProcessId,
            ["sha256"] = inventory.InputSha256,
            ["abi"] = inventory.SelectedAbi ?? options.RequestedAbi,
            ["bodyScope"] = options.GameplayFirst ? "gameplay-first" : "full"
        });

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
        var methods = BuildMethods(catalog, engine, binaryBytes, elf, options, cancellationToken, progress, trace, pace);
        var report = RecoveryReportBuilder.Build(methods, options.GameplayFirst ? "gameplay-first" : "full");
        Log("Writing output");
        OutputWriter.Write(outputDirectory, inventory, catalog, methods, report, logs, engine?.Diagnostic, engine?.Types);
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

    private static List<MethodRecord> BuildMethods(MetadataCatalog? catalog, EngineLoadResult? engine, byte[]? binary, ElfImage? elf, AnalysisOptions options, CancellationToken cancellationToken, IProgress<ProgressEvent>? progress, AnalysisTrace trace, Pace pace)
    {
        var records = new List<MethodRecord>();
        if (catalog is null)
            return records;

        var nativeByIndex = CallTargetIndex.IndexByMetadata(
            (engine?.Methods ?? []).Where(m => m.MetadataIndex >= 0),
            m => m.MetadataIndex,
            m => m.Image + " " + m.Namespace + "." + m.DeclaringType + "::" + m.Name + " va=0x" + m.VirtualAddress.ToString("X"));
        var catalogTypes = CallTargetIndex.IndexByMetadata(
            catalog.Images.SelectMany(i => i.Types),
            t => t.MetadataIndex,
            t => t.Namespace + "." + t.Name + " index=" + t.MetadataIndex);
        var ctx = BuildImageContext(catalog, catalogTypes, engine, nativeByIndex, binary, elf, options, cancellationToken, progress, trace, pace);
        var orderedAddresses = nativeByIndex.Values.Select(m => m.VirtualAddress).Where(a => a != 0).Distinct().Order().ToList();
        var framework = 0;
        var bodyTotal = 0;
        foreach (var image in catalog.Images)
        {
            foreach (var type in image.Types)
            {
                foreach (var method in type.Methods)
                {
                    if (options.GameplayFirst && RecoveryReportBuilder.IsFrameworkAssembly(image.Name) && method is { IsAbstract: false, IsExternal: false })
                        framework++;
                    else
                        bodyTotal++;
                }
            }
        }
        if (options.GameplayFirst)
        {
            var note = "Framework bodies not analyzed: " + framework + ". Gameplay-first mode did not lift them and does not count them as recovered.";
            progress?.Report(new ProgressEvent { Stage = "Body analysis", Detail = note, Kind = "stage" });
            trace.Write("scope", new Dictionary<string, object?> { ["frameworkNotAnalyzed"] = framework, ["bodiesAttempted"] = bodyTotal });
        }
        var done = 0;
        foreach (var image in catalog.Images)
        {
            foreach (var type in image.Types)
            {
                foreach (var method in type.Methods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var skip = options.GameplayFirst && RecoveryReportBuilder.IsFrameworkAssembly(image.Name) && method is { IsAbstract: false, IsExternal: false };
                    if (!skip)
                    {
                        done++;
                        if (done % 50 == 0 || done == bodyTotal)
                            progress?.Report(new ProgressEvent { Stage = "Methods", Detail = "body analysis started", Kind = "stage", Completed = done, Total = bodyTotal });
                    }
                    ctx.TypesByIndex.TryGetValue(type.MetadataIndex, out var typeInfo);
                    records.Add(BuildOne(image, type, method, nativeByIndex, orderedAddresses, binary, elf, options, typeInfo, ctx, skip, progress, trace, pace, cancellationToken));
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

    private sealed class Pace
    {
        public long Next = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private sealed class ImageContext
    {
        public CodeSpace? Space;
        public Dictionary<ulong, MethodContract> CallTargets = [];
        public int SharedNativeAddresses;
        public Dictionary<string, IReadOnlyList<FieldContract>> TypeFields = new(StringComparer.Ordinal);
        public Dictionary<int, EngineTypeInfo> TypesByIndex = [];
        public Dictionary<string, EngineTypeInfo> TypesByFullName = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<ulong, RuntimeHelper> Helpers = new Dictionary<ulong, RuntimeHelper>();
        public DiscoveryCache Cache { get; } = new(64);
    }

    private static string FullName(CatalogType type, IReadOnlyDictionary<int, CatalogType> all)
    {
        var parts = new List<string> { type.Name };
        var current = type;
        var guard = 0;
        while (current.DeclaringTypeIndex is int parent && all.TryGetValue(parent, out var outer) && guard++ < 32)
        {
            parts.Insert(0, outer.Name);
            current = outer;
        }
        var ns = current.Namespace;
        return (string.IsNullOrEmpty(ns) ? "" : ns + ".") + string.Join('+', parts);
    }

    private static string TopNamespace(CatalogType type, IReadOnlyDictionary<int, CatalogType> all)
    {
        var current = type;
        var guard = 0;
        while (current.DeclaringTypeIndex is int parent && all.TryGetValue(parent, out var outer) && guard++ < 32)
            current = outer;
        return current.Namespace;
    }

    private static ImageContext BuildImageContext(MetadataCatalog catalog, IReadOnlyDictionary<int, CatalogType> catalogTypes, EngineLoadResult? engine, Dictionary<int, EngineMethod> nativeByIndex, byte[]? binary, ElfImage? elf, AnalysisOptions options, CancellationToken cancellationToken, IProgress<ProgressEvent>? progress, AnalysisTrace trace, Pace pace)
    {
        var ctx = new ImageContext();
        foreach (var t in engine?.Types ?? [])
        {
            if (t.MetadataIndex < 0)
                continue;
            if (ctx.TypesByIndex.TryGetValue(t.MetadataIndex, out var existing))
            {
                throw new InvalidOperationException(
                    "Two engine types share metadata index " + t.MetadataIndex +
                    ". First: " + existing.Image + " " + existing.Namespace + "." + existing.Name +
                    ". Second: " + t.Image + " " + t.Namespace + "." + t.Name + ".");
            }
            ctx.TypesByIndex.Add(t.MetadataIndex, t);
        }
        foreach (var (index, info) in ctx.TypesByIndex)
        {
            if (!catalogTypes.TryGetValue(index, out var ct))
                continue;
            var name = FullName(ct, catalogTypes);
            if (ctx.TypesByFullName.TryGetValue(name, out var existing))
            {
                throw new InvalidOperationException(
                    "Two types share the name " + name +
                    ". First: " + existing.Image + " index " + existing.MetadataIndex +
                    ". Second: " + info.Image + " index " + info.MetadataIndex + ".");
            }
            ctx.TypesByFullName.Add(name, info);
        }
        foreach (var (name, info) in ctx.TypesByFullName)
            ctx.TypeFields[name] = LifterFields(info, ctx);

        if (binary is null || elf is null)
            return ctx;

        var entries = CallTargetIndex.ExecutableEntries(nativeByIndex.Values.Select(m => m.VirtualAddress)).ToList();
        ctx.Space = CodeSpace.ForElf(elf, binary, entries);
        var calleePairs = new List<(ulong Address, MethodContract Contract)>();
        foreach (var image in catalog.Images)
        {
            foreach (var type in image.Types)
            {
                var full = FullName(type, catalogTypes);
                var ns = TopNamespace(type, catalogTypes);
                var nestedName = string.IsNullOrEmpty(ns) ? full : full[(ns.Length + 1)..];
                foreach (var method in type.Methods)
                {
                    if (!nativeByIndex.TryGetValue(method.MetadataIndex, out var native) || native.VirtualAddress == 0)
                        continue;
                    calleePairs.Add((native.VirtualAddress, CalleeContract(image, nestedName, ns, method, native, ctx)));
                }
            }
        }
        var indexed = CallTargetIndex.Build(calleePairs);
        foreach (var (address, contract) in indexed.Resolved)
            ctx.CallTargets[address] = contract;
        ctx.SharedNativeAddresses = indexed.SharedAddresses;

        progress?.Report(new ProgressEvent { Stage = "Identifying runtime helpers", Detail = "pass 1", Kind = "helper", Completed = 0, Total = entries.Count });
        var scan = RuntimeHelperScan.Scan(ctx.Space, entries, cancellationToken, (done, total, phase) =>
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < pace.Next && done != total)
                return;
            pace.Next = now + System.Diagnostics.Stopwatch.Frequency;
            var stage = phase == "pass 1" ? "Identifying runtime helpers" : "Reanalyzing noreturn callers";
            progress?.Report(new ProgressEvent { Stage = stage, Detail = phase, Kind = "helper", Completed = done, Total = total });
            trace.Write("helper", new Dictionary<string, object?> { ["phase"] = phase, ["completed"] = done, ["total"] = total });
        });
        foreach (var note in scan.Notes)
            trace.Write("helper-note", new Dictionary<string, object?> { ["note"] = note });
        ctx.Helpers = scan.Helpers;
        trace.Write("helper-inference", new Dictionary<string, object?>
        {
            ["pass1"] = scan.Pass1Functions,
            ["pass2"] = scan.Pass2Functions,
            ["helpers"] = ctx.Helpers.Count,
            ["sharedNativeAddresses"] = ctx.SharedNativeAddresses,
            ["callTargets"] = ctx.CallTargets.Count
        });
        progress?.Report(new ProgressEvent
        {
            Stage = "Identifying runtime helpers",
            Detail = "inference finished, helpers=" + ctx.Helpers.Count + ", pass1=" + scan.Pass1Functions + ", pass2=" + scan.Pass2Functions,
            Kind = "helper",
            Completed = entries.Count,
            Total = entries.Count
        });
        return ctx;
    }

    private static bool TryKind(string text, ImageContext ctx, out PrimitiveKind kind, out string? why)
    {
        why = null;
        if (TryPrimitive(text, out kind))
            return true;
        var trimmed = text.Trim();
        if (trimmed is "System.Void" or "Void")
        {
            kind = PrimitiveKind.Void;
            return true;
        }
        if (trimmed.EndsWith('&') || trimmed.EndsWith('*'))
        {
            why = "by-reference or pointer type '" + trimmed + "'";
            return false;
        }
        if (trimmed is "unresolved" || trimmed.Length == 0 || trimmed is "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Char" or "Byte" or "SByte" or "Int16" or "UInt16" or "Char")
        {
            why = trimmed.Length == 0 || trimmed == "unresolved"
                ? "type was not resolved from the native type table"
                : "integer type '" + trimmed + "' is narrower than 32 bits and is not lifted";
            return false;
        }
        if (trimmed.EndsWith("[]", StringComparison.Ordinal) || trimmed is "System.String" or "System.Object" or "String" or "Object")
        {
            kind = PrimitiveKind.Reference;
            return true;
        }
        var definition = trimmed;
        var lt = definition.IndexOf('<');
        if (lt > 0 && !definition.StartsWith('<'))
            definition = definition[..lt];
        if (ctx.TypesByFullName.TryGetValue(definition, out var info))
        {
            if (info.IsEnum)
            {
                why = "enum type '" + trimmed + "' (enum values are not reconstructed in bodies yet)";
                return false;
            }
            if (info.IsValueType)
            {
                why = "value type '" + trimmed + "' (struct passing and returning are not modelled)";
                return false;
            }
            kind = PrimitiveKind.Reference;
            return true;
        }
        why = "type '" + trimmed + "' was not found in the loaded type table, so its kind is unknown";
        return false;
    }

    private static MethodContract CalleeContract(CatalogImage image, string declaringType, string ns, CatalogMethod method, EngineMethod native, ImageContext ctx)
    {
        string? unsupported = null;
        var parameters = new List<ParameterContract>();
        if (native.ParameterTypes.Count != method.Parameters.Count)
            unsupported = "parameter types were not all resolved";
        else
        {
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                if (!TryKind(native.ParameterTypes[i], ctx, out var k, out var why))
                {
                    unsupported ??= "parameter " + method.Parameters[i].Name + ": " + why;
                    k = PrimitiveKind.Reference;
                }
                parameters.Add(new ParameterContract { Name = method.Parameters[i].Name, Type = k, DeclaredType = native.ParameterTypes[i] });
            }
        }
        if (!TryKind(native.ReturnType, ctx, out var ret, out var retWhy))
        {
            unsupported ??= "return " + retWhy;
            ret = PrimitiveKind.Void;
        }
        return new MethodContract
        {
            Name = method.Name,
            DeclaringType = declaringType,
            Namespace = ns,
            Assembly = image.Name,
            IsStatic = method.IsStatic,
            IsVirtual = method.IsVirtual,
            IsAbstract = method.IsAbstract,
            IsGeneric = method.IsGeneric,
            ReturnType = ret,
            DeclaredReturnType = native.ReturnType,
            Parameters = parameters,
            UnsupportedReason = unsupported
        };
    }

    private static MethodRecord BuildOne(CatalogImage image, CatalogType type, CatalogMethod method, Dictionary<int, EngineMethod> nativeByIndex, List<ulong> orderedAddresses, byte[]? binary, ElfImage? elf, AnalysisOptions options, EngineTypeInfo? typeInfo, ImageContext ctx, bool skipBody, IProgress<ProgressEvent>? progress, AnalysisTrace trace, Pace pace, CancellationToken cancellationToken)
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
        string? internalStack = null;
        var boundary = BoundaryEvidence.NotApplicable;

        if (kind is MethodKind.Abstract or MethodKind.External)
            diagnostics.Add(kind + " method. No native body is expected, and none was invented.");
        else if (skipBody)
        {
            status = MethodRecoveryStatus.NotAnalyzed;
            diagnostics.Add("Gameplay-first analysis left this framework method body unanalyzed. Metadata and the call target stay available. Full analysis lifts framework bodies.");
            if (nativeByIndex.TryGetValue(method.MetadataIndex, out var skipped) && skipped.VirtualAddress != 0)
                address = "0x" + skipped.VirtualAddress.ToString("X");
        }
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
            else if (!TryContract(image, type, method, native, typeInfo, ctx, out var contract, out var why))
            {
                instructions.AddRange(Arm64Recoverer.Disassemble(code, native.VirtualAddress));
                status = instructions.Count > 0 ? MethodRecoveryStatus.Disassembled : MethodRecoveryStatus.MetadataOnly;
                diagnostics.Add(why);
            }
            else
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var detail = image.Name + " " + signature + " index=" + method.MetadataIndex + " addr=" + address;
                progress?.Report(new ProgressEvent { Stage = "Method start", Detail = detail, Kind = "method-start", Completed = method.MetadataIndex, Total = null });
                trace.Write("method-start", new Dictionary<string, object?>
                {
                    ["assembly"] = image.Name,
                    ["type"] = type.Name,
                    ["signature"] = signature,
                    ["metadataIndex"] = method.MetadataIndex,
                    ["address"] = address,
                    ["fileOffset"] = fileOffset,
                    ["sliceLength"] = length,
                    ["stage"] = "lift"
                });
                MethodBodyResult? lifted = null;
                try
                {
                    lifted = MethodIsolation.TryRecover(() => Arm64Recoverer.Recover(new RecoveryRequest
                    {
                        Code = code,
                        VirtualAddress = native.VirtualAddress,
                        Contract = contract,
                        Boundary = BoundaryEvidence.NextMethodAddressOnly,
                        Space = ctx.Space,
                        CallTargets = ctx.CallTargets,
                        TypeFields = ctx.TypeFields,
                        RuntimeHelpers = ctx.Helpers,
                        Budget = options.Budget,
                        CancellationToken = cancellationToken,
                        Cache = ctx.Cache,
                        Pulse = () =>
                        {
                            var now = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (now < pace.Next)
                                return;
                            pace.Next = now + System.Diagnostics.Stopwatch.Frequency;
                            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started, now);
                            progress?.Report(new ProgressEvent
                            {
                                Stage = "Body analysis",
                                Detail = detail + " elapsed=" + elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s",
                                Kind = "heartbeat"
                            });
                        }
                    }), out internalStack);
                }
                finally
                {
                    var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    trace.Write("method-end", new Dictionary<string, object?>
                    {
                        ["assembly"] = image.Name,
                        ["type"] = type.Name,
                        ["signature"] = signature,
                        ["metadataIndex"] = method.MetadataIndex,
                        ["address"] = address,
                        ["fileOffset"] = fileOffset,
                        ["sliceLength"] = length,
                        ["stage"] = "lift",
                        ["durationMs"] = elapsedMs,
                        ["instructions"] = lifted?.InstructionCount,
                        ["blocks"] = lifted?.BlockCount,
                        ["status"] = lifted?.Status.ToString() ?? "exception",
                        ["stack"] = internalStack
                    });
                }
                if (lifted is null)
                    throw new InvalidOperationException("The lifter returned no result.");
                instructions.AddRange(lifted.Instructions);
                diagnostics.AddRange(lifted.Diagnostics);
                status = lifted.Status;
                csharp = lifted.CSharp;
                placeholder = lifted.CSharpIsPlaceholder;
                candidate = lifted.CandidateCSharp;
                provenance.AddRange(lifted.Provenance);
                if (lifted.Boundary != BoundaryEvidence.NotApplicable)
                    boundary = lifted.Boundary;
                if (lifted.ThunkChain is not null)
                    kind = MethodKind.Thunk;
            }
        }

        if (csharp is null && options.EmitPlaceholders && kind is not (MethodKind.Abstract or MethodKind.External))
        {
            csharp = CSharpEmitter.Placeholder(status, diagnostics);
            placeholder = true;
        }

        return new MethodRecord
        {
            Assembly = image.Name,
            Namespace = type.Namespace,
            DeclaringType = type.Name,
            TypeMetadataIndex = type.MetadataIndex,
            Name = method.Name,
            Signature = signature,
            ReturnTypeName = returnTypeName,
            ParameterNames = method.Parameters.Select(p => p.Name).ToList(),
            ParameterTypeNames = parameterTypeNames,
            MetadataToken = method.Token,
            MetadataMethodIndex = method.MetadataIndex,
            IsStatic = method.IsStatic,
            Access = method.Access,
            IsVirtual = method.IsVirtual,
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
            Provenance = provenance,
            InternalStack = internalStack
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

    private static bool TryContract(CatalogImage image, CatalogType type, CatalogMethod method, EngineMethod native, EngineTypeInfo? typeInfo, ImageContext ctx, out MethodContract contract, out string error)
    {
        contract = new MethodContract { Name = method.Name, DeclaringType = type.Name, Namespace = type.Namespace, Assembly = image.Name, IsStatic = method.IsStatic };
        error = "";
        if (typeInfo is not null && typeInfo.IsValueType && !method.IsStatic)
        {
            error = "Instance method of a value type: 'this' is a managed pointer to the struct, which is not modelled.";
            return false;
        }
        if (!TryKind(native.ReturnType, ctx, out var ret, out var retWhy))
        {
            error = "Return type is outside the lifter subset: " + retWhy + ". No body was invented.";
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
            if (!TryKind(native.ParameterTypes[i], ctx, out var primitive, out var why))
            {
                error = "Parameter " + method.Parameters[i].Name + " is outside the lifter subset: " + why + ".";
                return false;
            }
            parameters.Add(new ParameterContract { Name = method.Parameters[i].Name, Type = primitive, DeclaredType = native.ParameterTypes[i] });
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
            DeclaredReturnType = native.ReturnType,
            Parameters = parameters,
            Fields = typeInfo is null ? [] : LifterFields(typeInfo, ctx)
        };
        return true;
    }

    private static IReadOnlyList<FieldContract> LifterFields(EngineTypeInfo typeInfo, ImageContext ctx)
    {
        var fields = new List<FieldContract>();
        foreach (var field in typeInfo.Fields)
        {
            if (field.IsStatic || field.IsLiteral || field.Offset is not int offset)
                continue;
            if (!TryKind(field.Type, ctx, out var primitive, out _) || primitive == PrimitiveKind.Void)
                continue;
            fields.Add(new FieldContract
            {
                Name = field.Name,
                Type = primitive,
                DeclaredType = field.Type,
                Access = field.Access,
                InstanceOffset = offset
            });
        }
        return fields;
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

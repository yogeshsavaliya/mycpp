namespace Il2CppRecover;

public sealed class ExtractionLimits
{
    public int MaxArchiveEntries { get; init; } = 100_000;
    public long MaxEntryUncompressedBytes { get; init; } = 512L * 1024 * 1024;
    public long MaxTotalUncompressedBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public double MaxCompressionRatio { get; init; } = 100;
    public long CompressionRatioMinCompressedBytes { get; init; } = 64 * 1024;
    public long MaxInputBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public static ExtractionLimits StrictTestDefaults() => new()
    {
        MaxArchiveEntries = 100,
        MaxEntryUncompressedBytes = 64 * 1024,
        MaxTotalUncompressedBytes = 256 * 1024,
        MaxCompressionRatio = 20,
        CompressionRatioMinCompressedBytes = 32,
        MaxInputBytes = 8L * 1024 * 1024
    };
}

public sealed class ArchiveEntryInfo
{
    public required string Name { get; init; }
    public long CompressedBytes { get; init; }
    public long UncompressedBytes { get; init; }
    public bool Rejected { get; init; }
    public string? Rejection { get; init; }
}

public sealed class ExtractedFile
{
    public required string ArchivePath { get; init; }
    public required string OutputPath { get; init; }
    public required string Sha256 { get; init; }
    public long SizeBytes { get; init; }
    public required string Role { get; init; }
}

public sealed class UnityVersionDetection
{
    public string? Version { get; init; }
    public string? Source { get; init; }
    public bool Ambiguous { get; init; }
    public IReadOnlyList<string> ObservedStrings { get; init; } = [];
}

public sealed class MetadataInspection
{
    public string? ArchivePath { get; init; }
    public string? Sha256 { get; init; }
    public long SizeBytes { get; init; }
    public bool MagicValid { get; init; }
    public uint? Magic { get; init; }
    public int? HeaderVersion { get; init; }
    public bool SuspectedProtection { get; init; }
    public string? Note { get; init; }
}

public sealed class NativeBinaryInspection
{
    public required string ArchivePath { get; init; }
    public required string Sha256 { get; init; }
    public long SizeBytes { get; init; }
    public string? Abi { get; init; }
    public bool Elf { get; init; }
    public bool Is64Bit { get; init; }
    public bool LittleEndian { get; init; }
    public int? Machine { get; init; }
    public NativeArchitecture Architecture { get; init; }
    public string? FileType { get; init; }
    public string? Note { get; init; }
}

public sealed class InventoryResult
{
    public required string ToolName { get; init; }
    public required string ToolVersion { get; init; }
    public required string InputPath { get; init; }
    public required string InputSha256 { get; init; }
    public long InputSizeBytes { get; init; }
    public required string InputKind { get; init; }
    public ScriptingBackend ScriptingBackend { get; init; }
    public UnityVersionDetection UnityVersion { get; init; } = new();
    public MetadataInspection? Metadata { get; init; }
    public IReadOnlyList<NativeBinaryInspection> NativeBinaries { get; init; } = [];
    public string? SelectedAbi { get; init; }
    public NativeBinaryInspection? SelectedBinary { get; init; }
    public IReadOnlyList<ExtractedFile> ExtractedFiles { get; init; } = [];
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public bool InputPreserved { get; init; } = true;
    public bool Succeeded { get; init; }
}

public sealed class CatalogParameter
{
    public required string Name { get; init; }
    public int MetadataIndex { get; init; }
    public string Type { get; init; } = "unresolved";
}

public sealed class CatalogField
{
    public required string Name { get; init; }
    public int MetadataIndex { get; init; }
    public string Type { get; init; } = "unresolved";
    public uint Token { get; init; }
}

public sealed class CatalogMethod
{
    public required string Name { get; init; }
    public int MetadataIndex { get; init; }
    public uint Token { get; init; }
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsVirtual { get; init; }
    public string Access { get; init; } = "public";
    public bool IsExternal { get; init; }
    public bool IsGeneric { get; init; }
    public int ParameterCount { get; init; }
    public int DeclaringTypeIndex { get; init; }
    public IReadOnlyList<CatalogParameter> Parameters { get; init; } = [];
    public string ReturnType { get; init; } = "unresolved";
    public string? NativeVirtualAddress { get; init; }
    public string? NativeFileOffset { get; init; }
}

public sealed class CatalogType
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }
    public int MetadataIndex { get; init; }
    public uint Token { get; init; }
    public bool IsValueType { get; init; }
    public bool IsEnum { get; init; }
    /// <summary>Metadata index of the declaring type for a nested type.</summary>
    public int? DeclaringTypeIndex { get; set; }
    public IReadOnlyList<CatalogField> Fields { get; init; } = [];
    public IReadOnlyList<CatalogMethod> Methods { get; init; } = [];
}

public sealed class CatalogImage
{
    public required string Name { get; init; }
    public IReadOnlyList<CatalogType> Types { get; init; } = [];
}

public sealed class MetadataCatalog
{
    public int HeaderVersion { get; init; }
    public float? LibCpp2IlLayoutVersion { get; init; }
    public string? LayoutUnityVersionUsed { get; init; }
    public string LayoutUnityVersionSource { get; init; } = "none";
    public IReadOnlyList<CatalogImage> Images { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public bool TypesResolvedFromBinary { get; init; }
}

public sealed class NativeInstruction
{
    public required string Address { get; init; }
    public required string Bytes { get; init; }
    public required string Text { get; init; }
}

public sealed class ProvenanceLink
{
    public required string Operation { get; init; }
    public required IReadOnlyList<string> NativeAddresses { get; init; }
}

public sealed class MethodRecord
{
    public required string Assembly { get; init; }
    public required string Namespace { get; init; }
    public required string DeclaringType { get; init; }
    public required string Name { get; init; }
    public required string Signature { get; init; }
    /// <summary>Metadata index of the declaring type. Names alone are not unique for nested types.</summary>
    public int? TypeMetadataIndex { get; init; }
    public string ReturnTypeName { get; init; } = "unresolved";
    public IReadOnlyList<string> ParameterNames { get; init; } = [];
    public IReadOnlyList<string> ParameterTypeNames { get; init; } = [];
    public uint? MetadataToken { get; init; }
    public int? MetadataMethodIndex { get; init; }
    public bool IsStatic { get; init; }
    public string Access { get; init; } = "public";
    public bool IsVirtual { get; init; }
    public MethodKind MethodKind { get; set; }
    public string? NativeVirtualAddress { get; init; }
    public string? NativeFileOffset { get; init; }
    public BoundaryEvidence BoundaryEvidence { get; init; }
    public MethodRecoveryStatus Status { get; init; }
    public string Compilation { get; init; } = "NotChecked";
    public string Behaviour { get; init; } = "NotChecked";
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public IReadOnlyList<NativeInstruction> Instructions { get; init; } = [];
    public string? CSharp { get; init; }
    public bool CSharpIsPlaceholder { get; init; }
    public string? CandidateCSharp { get; init; }
    public IReadOnlyList<ProvenanceLink> Provenance { get; init; } = [];
    public int SharedImplementationCount { get; set; } = 1;
    /// <summary>True when this recovered body and every gameplay method it calls directly are recovered. False when the body or a gameplay callee is not. Null on records built outside analysis.</summary>
    public bool? FeatureComplete { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? InternalStack { get; init; }
}

public sealed class RecoveryCounts
{
    public int MethodsInMetadata { get; init; }
    public int MethodsWithNativeAddress { get; init; }
    public int UniqueNativeAddresses { get; init; }
    public int AbstractOrExternalMethods { get; init; }
    public int MethodsSharingANativeAddress { get; init; }
    public int FullyRecoveredUniqueBodies { get; init; }
    public int PartiallyRecovered { get; init; }
    public int DisassembledOnly { get; init; }
    public int MetadataOnly { get; init; }
    public int Unsupported { get; init; }
    public int Failed { get; init; }
    public int PlaceholderBodies { get; init; }
    public int AnalysisBudgetExceeded { get; init; }
    public int TimedOut { get; init; }
    public int NotAnalyzed { get; init; }
    public int InternalErrors { get; init; }
    public int SharedNativeAddresses { get; init; }
    public int SameNameTypeGroups { get; init; }
    public int CompilationChecked { get; init; }
    public int BehaviourCheckedOnFixtures { get; init; }
    public required string Denominator { get; init; }
}

public sealed class StageBreakdown
{
    public required string Scope { get; init; }
    public IReadOnlyList<string> Assemblies { get; init; } = [];
    public int MethodsDiscovered { get; init; }
    public int MappedToNativeCode { get; init; }
    public int Disassembled { get; init; }
    public int CandidateBodies { get; init; }
    public int EmittedWithoutPlaceholder { get; init; }
    public int PassedCompilationCheck { get; init; }
    public int PassedBehaviouralCheck { get; init; }
    /// <summary>Recovered methods whose direct and tail calls to gameplay methods are recovered too. Framework calls are external.</summary>
    public int FeatureComplete { get; init; }
    public IReadOnlyDictionary<string, int> UnrecoveredByReason { get; init; } = new Dictionary<string, int>();
}

public sealed class RecoveryReport
{
    public required string ToolName { get; init; }
    public required string ToolVersion { get; init; }
    public required string RecoveryEngine { get; init; }
    public required string RecoveryEngineVersion { get; init; }
    public required string PinnedCpp2IlCommit { get; init; }
    public required string PinnedCpp2IlCore { get; init; }
    public required string PinnedLibCpp2Il { get; init; }
    public required string PinnedDisarm { get; init; }
    public required RecoveryCounts Counts { get; init; }
    public string BuildIdentity { get; init; } = "";
    /// <summary>full, or gameplay-first. Framework methods are not dropped from metadata in either mode.</summary>
    public string BodyAnalysisScope { get; init; } = "full";
    public IReadOnlyList<StageBreakdown> Breakdown { get; init; } = [];
    public required string BehaviouralValidation { get; init; }
    public IReadOnlyList<string> InternalErrorSamples { get; init; } = [];
    public required IReadOnlyList<string> Limitations { get; init; }
}

public sealed class ProgressEvent
{
    public required string Stage { get; init; }
    public string? Detail { get; init; }
    public int? Completed { get; init; }
    public int? Total { get; init; }
    /// <summary>stage, helper, heartbeat, method-start, method-end, or identity. Empty is treated as stage.</summary>
    public string Kind { get; init; } = "";
}

public sealed class AnalysisOutput
{
    public required InventoryResult Inventory { get; init; }
    public MetadataCatalog? Catalog { get; init; }
    public IReadOnlyList<MethodRecord> Methods { get; init; } = [];
    public required RecoveryReport Report { get; init; }
    public required string OutputDirectory { get; init; }
    public IReadOnlyList<string> Logs { get; init; } = [];
    public string? EngineLoadDiagnostic { get; init; }
}

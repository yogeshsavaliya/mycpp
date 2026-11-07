namespace Il2CppRecover;

/// <summary>
/// What was actually produced for one method. These are not a quality score.
/// CompilationChecked and BehaviourCheckedOnFixtures are never inferred from a
/// successful lift; a caller must record those checks separately.
/// </summary>
public enum MethodRecoveryStatus
{
    MetadataOnly = 0,
    Disassembled,
    PartiallyRecovered,
    RecoveredUnvalidated,
    CompilationChecked,
    BehaviourCheckedOnFixtures,
    Unsupported,
    Failed,
    /// <summary>A configured instruction, block, or structuring budget stopped this method. It is not a recovered body.</summary>
    AnalysisBudgetExceeded,
    /// <summary>The per-method deadline elapsed. It is not a recovered body.</summary>
    TimedOut,
    /// <summary>Body analysis was not run for this method. Metadata is still recorded.</summary>
    NotAnalyzed
}

public enum ScriptingBackend
{
    Unknown = 0,
    Il2Cpp,
    Mono,
    Mixed
}

public enum NativeArchitecture
{
    Unknown = 0,
    Arm64,
    Arm32,
    X64,
    X86,
    Other
}

/// <summary>
/// Why the byte slice was treated as a method body.
/// NextMethodAddressOnly is a heuristic and is not promoted to recovered code.
/// </summary>
public enum BoundaryEvidence
{
    NotApplicable = 0,
    CallerSuppliedSlice,
    EndsWithReturn,
    NextMethodAddressOnly,
    Uncertain,
    /// <summary>
    /// Recursive descent from the entry reached only RET, a tail call to a known method entry,
    /// or a call with noreturn evidence, and never crossed the next known entry.
    /// </summary>
    ControlFlowClosed
}

public enum MethodKind
{
    Unknown = 0,
    Concrete,
    Abstract,
    External,
    SharedNative,
    Thunk,
    Generic
}

public enum PrimitiveKind
{
    Void = 0,
    Boolean,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Single,
    Double,
    Reference
}

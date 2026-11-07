namespace Il2CppRecover.Recovery;

public abstract record Expr(PrimitiveKind Type)
{
    /// <summary>Metadata type name of a reference-typed value, when known. Display only.</summary>
    public string? RefType { get; init; }
    /// <summary>Type-definition index of a reference-typed value, when the value is a definition or a generic instance.</summary>
    public int? RefTypeId { get; init; }
    public virtual string Describe() => ToString() ?? Type.ToString();
}

public sealed record CastExpr(Expr Value, PrimitiveKind Target) : Expr(Target)
{
    public override string Describe() => $"({Target}){Value.Describe()}";
}

public sealed record FloatConstExpr(double Value, PrimitiveKind FloatType) : Expr(FloatType)
{
    public override string Describe() => Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record OpaqueExpr(string Tag) : Expr(PrimitiveKind.UInt64)
{
    public override string Describe() => "<" + Tag + ">";
}

public sealed record StackAddrExpr(long Offset) : Expr(PrimitiveKind.UInt64)
{
    public override string Describe() => $"<sp{(Offset < 0 ? "-" : "+")}0x{Math.Abs(Offset):X}>";
}

public sealed record FieldAddrExpr(Expr Base, long Offset) : Expr(PrimitiveKind.UInt64)
{
    public override string Describe() => $"&({Base.Describe()} + 0x{Offset:X})";
}

public sealed record ParamExpr(string Name, PrimitiveKind Type) : Expr(Type)
{
    public override string Describe() => Name;
}

public sealed record ThisExpr() : Expr(PrimitiveKind.Reference)
{
    public override string Describe() => "this";
}

public sealed record ConstExpr(long Value, PrimitiveKind Type) : Expr(Type)
{
    public override string Describe() => Value.ToString();
}

public sealed record LocalExpr(string Name, PrimitiveKind Type) : Expr(Type)
{
    public override string Describe() => Name;
}

public sealed record BinaryExpr(string Op, Expr Left, Expr Right, PrimitiveKind Type) : Expr(Type)
{
    public override string Describe() => $"({Left.Describe()} {Op} {Right.Describe()})";
}

public sealed record UnaryExpr(string Op, Expr Value, PrimitiveKind Type) : Expr(Type)
{
    public override string Describe() => $"({Op}{Value.Describe()})";
}

public sealed record FieldExpr(Expr? Instance, string Name, PrimitiveKind Type) : Expr(Type)
{
    public override string Describe() => Instance is null ? Name : $"{Instance.Describe()}.{Name}";
}

public sealed record CallExpr(string Name, IReadOnlyList<Expr> Args, PrimitiveKind Type) : Expr(Type)
{
    public Expr? Receiver { get; init; }
    public string? StaticType { get; init; }
    public ulong? Target { get; init; }
    /// <summary>A direct call on this to a virtual or abstract method declared on another type.</summary>
    public bool BaseCall { get; init; }
    public override string Describe() => (Receiver is null ? StaticType is null ? "" : StaticType + "." : Receiver.Describe() + ".") + $"{Name}({string.Join(", ", Args.Select(a => a.Describe()))})";
}

public sealed record ConditionalExpr(Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr(WhenTrue.Type)
{
    public override string Describe() => $"({Condition.Describe()} ? {WhenTrue.Describe()} : {WhenFalse.Describe()})";
}

public sealed record AddressExpr(ulong Address) : Expr(PrimitiveKind.UInt64)
{
    public override string Describe() => $"0x{Address:X}";
}

public enum MetadataSlotKind { Type, Method, Field, StringLiteral }

/// <summary>
/// A resolved IL2CPP runtime-metadata slot. <see cref="ValueAtAddress"/> is false when the word at
/// the loaded address is a pointer to the encoded token (the v29 layout) and a following load of
/// that pointer yields the resolved entity.
/// </summary>
public sealed record MetadataSlot(MetadataSlotKind Kind, string Display, bool ValueAtAddress);

public sealed record MetadataEntryExpr(MetadataSlot Slot) : Expr(PrimitiveKind.Reference)
{
    public override string Describe() => "metadata entry " + Slot.Kind + " " + Slot.Display;
}

public sealed record MetadataValueExpr(MetadataSlot Slot) : Expr(PrimitiveKind.Reference)
{
    public override string Describe() => Slot.Kind + " " + Slot.Display;
}

public abstract record Stmt
{
    public required IReadOnlyList<ulong> Addresses { get; init; }
}

public sealed record ReturnStmt(Expr? Value) : Stmt;
public sealed record AssignStmt(string Local, Expr Value) : Stmt;
public sealed record StoreFieldStmt(Expr? Instance, string Field, Expr Value) : Stmt;
public sealed record IfStmt(Expr Condition, List<Stmt> WhenTrue, List<Stmt> WhenFalse) : Stmt;
public sealed record WhileStmt(Expr Condition, List<Stmt> Body) : Stmt;
public sealed record ThrowStmt(string ExceptionType, string Evidence) : Stmt;
public sealed record CtorInitializerStmt(bool IsBase, IReadOnlyList<Expr> Args) : Stmt;

public sealed class ParameterContract
{
    public required string Name { get; init; }
    public required PrimitiveKind Type { get; init; }
    public string? DeclaredType { get; init; }
    public int? DeclaredTypeId { get; init; }
}

public sealed class FieldContract
{
    public required string Name { get; init; }
    public required PrimitiveKind Type { get; init; }
    public bool IsStatic { get; init; }
    public string Access { get; init; } = "public";
    public string? DeclaredType { get; init; }
    public int? DeclaredTypeId { get; init; }
    public string? OwnerType { get; init; }
    public int? OwnerTypeId { get; init; }
    public int? InstanceOffset { get; init; }
    public ulong? AbsoluteAddress { get; init; }
}

public sealed class MethodContract
{
    public required string Name { get; init; }
    public string Namespace { get; init; } = "";
    public required string DeclaringType { get; init; }
    public string Assembly { get; init; } = "Assembly-CSharp";
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsVirtual { get; init; }
    public string Access { get; init; } = "public";
    public bool IsGeneric { get; init; }
    public PrimitiveKind ReturnType { get; init; }
    public string? DeclaredReturnType { get; init; }
    public int? DeclaredReturnTypeId { get; init; }
    public int? DeclaringTypeId { get; init; }
    public IReadOnlyList<ParameterContract> Parameters { get; init; } = [];
    public IReadOnlyList<FieldContract> Fields { get; init; } = [];
    /// <summary>When set, a call to this method is not lifted. The reason names the missing ABI fact.</summary>
    public string? UnsupportedReason { get; init; }
}

public sealed class RecoveryRequest
{
    public required byte[] Code { get; init; }
    public ulong VirtualAddress { get; init; }
    public required MethodContract Contract { get; init; }
    public BoundaryEvidence Boundary { get; init; } = BoundaryEvidence.CallerSuppliedSlice;
    public IReadOnlyDictionary<ulong, MethodContract>? CallTargets { get; init; }
    public CodeSpace? Space { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<FieldContract>>? TypeFields { get; init; }
    public IReadOnlyDictionary<int, IReadOnlyList<FieldContract>>? TypeFieldsById { get; init; }
    public IReadOnlyDictionary<int, string>? LayoutNotes { get; init; }
    public IReadOnlyDictionary<ulong, RuntimeHelper>? RuntimeHelpers { get; init; }
    /// <summary>Resolves one absolute address to an IL2CPP metadata slot, or null when the bytes are not a token.</summary>
    public Func<ulong, MetadataSlot?>? ResolveMetadataSlot { get; init; }
    public AnalysisBudget Budget { get; init; } = AnalysisBudget.Default;
    public CancellationToken CancellationToken { get; init; }
    public DiscoveryCache? Cache { get; init; }
    /// <summary>Stopwatch timestamp at which the method deadline has elapsed. Null means no deadline.</summary>
    public long? DeadlineTimestamp { get; set; }
    /// <summary>Called from long loops so the host can publish a heartbeat. It must be cheap.</summary>
    public Action? Pulse { get; init; }
}

public sealed class AnalysisBudget
{
    public static AnalysisBudget Default { get; } = new();

    /// <summary>Instruction cap for one discovery. The historical limit stays in force unless a caller sets a smaller budget.</summary>
    public int MaxInstructions { get; init; } = FunctionDiscovery.MaxInstructions;

    /// <summary>Basic-block cap before structuring. A method over this cap is not recovered.</summary>
    public int MaxBlocks { get; init; } = 16384;

    public TimeSpan MethodDeadline { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class AnalysisLimitException(MethodRecoveryStatus status, string message) : Exception(message)
{
    public MethodRecoveryStatus Status { get; } = status;
}

public enum RuntimeHelperKind { RaiseNullReferenceException }

public sealed record RuntimeHelper(RuntimeHelperKind Kind, string Evidence);

public sealed class MethodBodyResult
{
    public required MethodRecoveryStatus Status { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
    public required IReadOnlyList<NativeInstruction> Instructions { get; init; }
    public string? CSharp { get; init; }
    public bool CSharpIsPlaceholder { get; init; }
    public string? CandidateCSharp { get; init; }
    public IReadOnlyList<ProvenanceLink> Provenance { get; init; } = [];
    public IReadOnlyList<Stmt> Statements { get; init; } = [];
    public BoundaryEvidence Boundary { get; init; } = BoundaryEvidence.NotApplicable;
    public string Engine { get; init; } = "";
    public string? ThunkChain { get; init; }
    public int InstructionCount { get; init; }
    public int BlockCount { get; init; }
}

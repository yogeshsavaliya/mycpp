namespace Il2CppRecover.Recovery;

public abstract record Expr(PrimitiveKind Type)
{
    public virtual string Describe() => ToString() ?? Type.ToString();
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
    public override string Describe() => $"{Name}({string.Join(", ", Args.Select(a => a.Describe()))})";
}

public sealed record ConditionalExpr(Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr(WhenTrue.Type)
{
    public override string Describe() => $"({Condition.Describe()} ? {WhenTrue.Describe()} : {WhenFalse.Describe()})";
}

public sealed record AddressExpr(ulong Address) : Expr(PrimitiveKind.UInt64)
{
    public override string Describe() => $"0x{Address:X}";
}

public abstract record Stmt
{
    public required IReadOnlyList<ulong> Addresses { get; init; }
}

public sealed record ReturnStmt(Expr? Value) : Stmt;
public sealed record AssignStmt(string Local, Expr Value) : Stmt;
public sealed record StoreFieldStmt(Expr? Instance, string Field, Expr Value) : Stmt;
public sealed record IfStmt(Expr Condition, IReadOnlyList<Stmt> WhenTrue, IReadOnlyList<Stmt> WhenFalse) : Stmt;
public sealed record WhileStmt(Expr Condition, IReadOnlyList<Stmt> Body) : Stmt;

public sealed class ParameterContract
{
    public required string Name { get; init; }
    public required PrimitiveKind Type { get; init; }
}

public sealed class FieldContract
{
    public required string Name { get; init; }
    public required PrimitiveKind Type { get; init; }
    public bool IsStatic { get; init; }
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
    public bool IsGeneric { get; init; }
    public PrimitiveKind ReturnType { get; init; }
    public IReadOnlyList<ParameterContract> Parameters { get; init; } = [];
    public IReadOnlyList<FieldContract> Fields { get; init; } = [];
}

public sealed class RecoveryRequest
{
    public required byte[] Code { get; init; }
    public ulong VirtualAddress { get; init; }
    public required MethodContract Contract { get; init; }
    public BoundaryEvidence Boundary { get; init; } = BoundaryEvidence.CallerSuppliedSlice;
    public IReadOnlyDictionary<ulong, MethodContract>? CallTargets { get; init; }
}

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
}

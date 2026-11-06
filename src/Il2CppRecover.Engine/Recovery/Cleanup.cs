namespace Il2CppRecover.Recovery;

/// <summary>Semantics-preserving expression rewrites. Float comparisons are never inverted, because of NaN.</summary>
public static class Simplifier
{
    private static bool IsFloat(Expr e) => e.Type is PrimitiveKind.Single or PrimitiveKind.Double;

    public static Expr Not(Expr e)
    {
        if (e is UnaryExpr { Op: "!" } u)
            return u.Value;
        if (e is ConstExpr { Type: PrimitiveKind.Boolean } c)
            return new ConstExpr(c.Value == 0 ? 1 : 0, PrimitiveKind.Boolean);
        if (e is BinaryExpr b && !IsFloat(b.Left) && !IsFloat(b.Right))
        {
            var inverted = b.Op switch
            {
                "==" => "!=", "!=" => "==", "<" => ">=", ">=" => "<", ">" => "<=", "<=" => ">",
                "u<" => "u>=", "u>=" => "u<", "u>" => "u<=", "u<=" => "u>",
                _ => null
            };
            if (inverted is not null)
                return new BinaryExpr(inverted, b.Left, b.Right, PrimitiveKind.Boolean);
        }
        return new UnaryExpr("!", e, PrimitiveKind.Boolean);
    }

    public static Expr Fold(Expr e)
    {
        switch (e)
        {
            case BinaryExpr { Left: ConstExpr l, Right: ConstExpr r } b when b.Type is PrimitiveKind.Int32 or PrimitiveKind.Int64:
            {
                var is32 = b.Type == PrimitiveKind.Int32;
                long? v = b.Op switch
                {
                    "+" => l.Value + r.Value,
                    "-" => l.Value - r.Value,
                    "*" => l.Value * r.Value,
                    "&" => l.Value & r.Value,
                    "|" => l.Value | r.Value,
                    "^" => l.Value ^ r.Value,
                    _ => null
                };
                if (v is null)
                    return e;
                return new ConstExpr(is32 ? unchecked((int)v.Value) : v.Value, b.Type);
            }
            case BinaryExpr { Right: ConstExpr { Value: 0 } } b when b.Op is "+" or "-" or "|" or "^" or "<<" or ">>" or ">>>" && b.Left.Type == b.Type:
                return b.Left;
            case UnaryExpr { Op: "~", Value: ConstExpr c } u:
                return new ConstExpr(u.Type == PrimitiveKind.Int32 ? unchecked((int)~c.Value) : ~c.Value, u.Type);
            case UnaryExpr { Op: "-", Value: ConstExpr c } u when u.Type is PrimitiveKind.Int32 or PrimitiveKind.Int64:
                return new ConstExpr(u.Type == PrimitiveKind.Int32 ? unchecked((int)-c.Value) : unchecked(-c.Value), u.Type);
            case ConditionalExpr { Condition: ConstExpr { Type: PrimitiveKind.Boolean } c } ce:
                return c.Value != 0 ? ce.WhenTrue : ce.WhenFalse;
            case ConditionalExpr ce when Equals(ce.WhenTrue, ce.WhenFalse):
                return ce.WhenTrue;
        }
        return e;
    }

    public static Expr Assume(Expr e, Expr condition, bool value)
    {
        var negated = Not(condition);
        Expr Walk(Expr x)
        {
            switch (x)
            {
                case ConditionalExpr ce:
                    if (Equals(ce.Condition, condition))
                        return Walk(value ? ce.WhenTrue : ce.WhenFalse);
                    if (Equals(ce.Condition, negated))
                        return Walk(value ? ce.WhenFalse : ce.WhenTrue);
                    return ce with { Condition = Walk(ce.Condition), WhenTrue = Walk(ce.WhenTrue), WhenFalse = Walk(ce.WhenFalse) };
                case BinaryExpr b:
                    if (Equals(b, condition))
                        return new ConstExpr(value ? 1 : 0, PrimitiveKind.Boolean);
                    return Fold(b with { Left = Walk(b.Left), Right = Walk(b.Right) });
                case UnaryExpr u:
                    return Fold(u with { Value = Walk(u.Value) });
                case CastExpr c:
                    return c with { Value = Walk(c.Value) };
                default:
                    return x;
            }
        }
        return Walk(e);
    }
}

/// <summary>
/// Inlines a single-use temporary only when that cannot reorder a side effect, and drops unread pure locals.
/// </summary>
public static class Cleanup
{
    public static void Run(List<Stmt> body, Action? check = null)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            check?.Invoke();
            var counts = new Dictionary<string, int>();
            CountReads(body, counts);
            var changed = Inline(body, counts, check);
            changed |= RemoveDead(body);
            if (!changed)
                break;
        }
    }

    public static void CountReads(IEnumerable<Stmt> stmts, Dictionary<string, int> counts)
    {
        foreach (var s in stmts)
        {
            foreach (var e in TopExprs(s))
                CountExpr(e, counts);
            foreach (var child in Children(s))
                CountReads(child, counts);
        }
    }

    private static void CountExpr(Expr e, Dictionary<string, int> counts)
    {
        foreach (var x in Walk(e))
            if (x is LocalExpr l)
                counts[l.Name] = counts.GetValueOrDefault(l.Name) + 1;
    }

    public static IEnumerable<Expr> Walk(Expr root)
    {
        var stack = new Stack<Expr>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            switch (e)
            {
                case BinaryExpr b:
                    stack.Push(b.Right);
                    stack.Push(b.Left);
                    break;
                case UnaryExpr u:
                    stack.Push(u.Value);
                    break;
                case CastExpr c:
                    stack.Push(c.Value);
                    break;
                case FieldExpr { Instance: not null } f:
                    stack.Push(f.Instance);
                    break;
                case CallExpr call:
                    for (var i = call.Args.Count - 1; i >= 0; i--)
                        stack.Push(call.Args[i]);
                    if (call.Receiver is not null)
                        stack.Push(call.Receiver);
                    break;
                case ConditionalExpr ce:
                    stack.Push(ce.WhenFalse);
                    stack.Push(ce.WhenTrue);
                    stack.Push(ce.Condition);
                    break;
                case FieldAddrExpr fa:
                    stack.Push(fa.Base);
                    break;
                case SelectedOffsetExpr so:
                    stack.Push(so.Condition);
                    stack.Push(so.Base);
                    break;
            }
        }
    }

    public static IEnumerable<Expr> TopExprs(Stmt s) => s switch
    {
        DeclareStmt d => [d.Value],
        AssignStmt a => [a.Value],
        ReturnStmt { Value: not null } r => [r.Value],
        StoreFieldStmt sf => sf.Instance is null ? [sf.Value] : [sf.Instance, sf.Value],
        ExpressionStmt x => [x.Value],
        IfStmt i => [i.Condition],
        WhileStmt w => [w.Condition],
        CtorInitializerStmt c => c.Args,
        _ => []
    };

    private static IEnumerable<List<Stmt>> Children(Stmt s) => s switch
    {
        IfStmt i => [AsList(i.WhenTrue), AsList(i.WhenFalse)],
        WhileStmt w => [AsList(w.Body)],
        _ => []
    };

    private static List<Stmt> AsList(IReadOnlyList<Stmt> stmts) =>
        stmts as List<Stmt> ?? throw new InvalidOperationException("Structured statements must be built as List<Stmt> so cleanup can rewrite them in place.");

    private static bool HasCall(Expr e) => Walk(e).Any(x => x is CallExpr);

    private static bool Inline(List<Stmt> list, Dictionary<string, int> counts, Action? check)
    {
        var changed = false;
        foreach (var s in list)
            foreach (var child in Children(s))
                changed |= Inline(child, counts, check);

        var firstTop = new Dictionary<string, int>();
        var firstChild = new Dictionary<string, int>();
        for (var k = 0; k < list.Count; k++)
        {
            if ((k & 63) == 0)
                check?.Invoke();
            foreach (var name in TopLocals(list[k]))
                firstTop.TryAdd(name, k);
            foreach (var child in Children(list[k]))
            {
                foreach (var name in LocalsIn(child))
                    firstChild.TryAdd(name, k);
            }
        }
        for (var i = 0; i < list.Count; i++)
        {
            if ((i & 63) == 0)
                check?.Invoke();
            if (list[i] is not DeclareStmt d || !d.Local.StartsWith('t') || d.Value is DefaultExpr)
                continue;
            if (counts.GetValueOrDefault(d.Local) != 1)
                continue;
            if (!firstTop.TryGetValue(d.Local, out var j) || j <= i)
                continue;
            if (firstChild.TryGetValue(d.Local, out var childAt) && childAt > i && childAt < j)
                continue;
            if (!SafeToMove(d.Value, list, i, j))
                continue;
            list[j] = Replace(list[j], d.Local, d.Value);
            list.RemoveAt(i);
            Shift(firstTop, i);
            Shift(firstChild, i);
            i--;
            changed = true;
            counts[d.Local] = counts.GetValueOrDefault(d.Local) - 1;
        }
        return changed;
    }

    private static void Shift(Dictionary<string, int> map, int removed)
    {
        foreach (var key in map.Keys.ToArray())
        {
            var index = map[key];
            if (index == removed)
                map.Remove(key);
            else if (index > removed)
                map[key] = index - 1;
        }
    }

    private static IEnumerable<string> TopLocals(Stmt statement)
    {
        foreach (var expression in TopExprs(statement))
        {
            foreach (var node in Walk(expression))
            {
                if (node is LocalExpr local)
                    yield return local.Name;
            }
        }
    }

    private static IEnumerable<string> LocalsIn(List<Stmt> stmts)
    {
        var counts = new Dictionary<string, int>();
        CountReads(stmts, counts);
        return counts.Keys;
    }

    private static bool SafeToMove(Expr value, List<Stmt> list, int from, int to)
    {
        var reads = Walk(value).OfType<LocalExpr>().Select(l => l.Name).ToHashSet();
        var target = list[to];
        if (HasCall(value))
        {
            if (to != from + 1)
                return false;
            return target switch
            {
                ReturnStmt { Value: LocalExpr } => true,
                DeclareStmt { Value: LocalExpr } => true,
                AssignStmt { Value: LocalExpr } => true,
                StoreFieldStmt { Instance: null or ThisExpr or LocalExpr, Value: LocalExpr } sf => sf.Instance is not LocalExpr || !HasCall(sf.Instance),
                IfStmt { Condition: BinaryExpr { Left: LocalExpr, Right: ConstExpr or NullExpr } } => true,
                IfStmt { Condition: LocalExpr } => true,
                _ => false
            };
        }
        for (var k = from + 1; k < to; k++)
        {
            if (list[k] is not DeclareStmt between || HasCall(between.Value))
                return false;
        }
        if (target is AssignStmt a && reads.Contains(a.Local))
            return false;
        foreach (var e in TopExprs(target))
        {
            if (!HasCall(e))
                continue;
            if (e is CallExpr outer && !(outer.Receiver is not null && HasCall(outer.Receiver)) && !outer.Args.Any(HasCall))
                continue;
            return false;
        }
        return true;
    }

    private static Stmt Replace(Stmt s, string name, Expr with)
    {
        Expr R(Expr e) => Substitute(e, name, with);
        return s switch
        {
            DeclareStmt d => d with { Value = R(d.Value) },
            AssignStmt a => a with { Value = R(a.Value) },
            ReturnStmt { Value: not null } r => r with { Value = R(r.Value) },
            StoreFieldStmt sf => sf with { Instance = sf.Instance is null ? null : R(sf.Instance), Value = R(sf.Value) },
            ExpressionStmt x => x with { Value = R(x.Value) },
            IfStmt i => i with { Condition = R(i.Condition) },
            CtorInitializerStmt c => c with { Args = c.Args.Select(R).ToList() },
            _ => s
        };
    }

    public static Expr Substitute(Expr e, string name, Expr with) => e switch
    {
        LocalExpr l when l.Name == name => with,
        BinaryExpr b => b with { Left = Substitute(b.Left, name, with), Right = Substitute(b.Right, name, with) },
        UnaryExpr u => u with { Value = Substitute(u.Value, name, with) },
        CastExpr c => c with { Value = Substitute(c.Value, name, with) },
        FieldExpr { Instance: not null } f => f with { Instance = Substitute(f.Instance, name, with) },
        CallExpr call => call with { Receiver = call.Receiver is null ? null : Substitute(call.Receiver, name, with), Args = call.Args.Select(a => Substitute(a, name, with)).ToList() },
        ConditionalExpr ce => ce with { Condition = Substitute(ce.Condition, name, with), WhenTrue = Substitute(ce.WhenTrue, name, with), WhenFalse = Substitute(ce.WhenFalse, name, with) },
        _ => e
    };

    private static bool IsPure(Expr e) => Walk(e).All(x => x is not CallExpr && x is not FieldExpr { Instance: not (null or ThisExpr) });

    private static bool RemoveDead(List<Stmt> root)
    {
        var counts = new Dictionary<string, int>();
        CountReads(root, counts);
        return RemoveDeadIn(root, counts);
    }

    private static bool RemoveDeadIn(List<Stmt> list, Dictionary<string, int> counts)
    {
        var changed = false;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var s = list[i];
            foreach (var child in Children(s))
                changed |= RemoveDeadIn(child, counts);
            var dead = s switch
            {
                DeclareStmt d => counts.GetValueOrDefault(d.Local) == 0 && IsPure(d.Value),
                AssignStmt a => counts.GetValueOrDefault(a.Local) == 0 && IsPure(a.Value),
                _ => false
            };
            if (dead)
            {
                list.RemoveAt(i);
                changed = true;
            }
        }
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is IfStmt { WhenTrue.Count: 0, WhenFalse.Count: 0 } empty && IsPure(empty.Condition))
            {
                list.RemoveAt(i);
                changed = true;
            }
        }
        return changed;
    }
}

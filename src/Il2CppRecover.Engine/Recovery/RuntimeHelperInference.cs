namespace Il2CppRecover.Recovery;

/// <summary>
/// Identifies the IL2CPP null-reference raise helper in a stripped binary from call-site shape.
/// A noreturn callee is labelled only when at least <see cref="MinimumSites"/> sites, and at least
/// <see cref="MinimumShare"/> of its noreturn sites, are reached only by CBZ/CBNZ on a register,
/// pass no arguments, and the other path dereferences that register. Two helpers with the same
/// shape are left unlabelled.
/// </summary>
public readonly record struct HelperObservation(ulong Target, bool NullCheckShape);

public static class RuntimeHelperInference
{
    public const int MinimumSites = 3;
    public const double MinimumShare = 0.9;
    private const int LookAhead = 8;

    public static IReadOnlyDictionary<ulong, RuntimeHelper> InferNullReferenceRaiser(IEnumerable<DiscoveredFunction> functions)
    {
        var observations = new List<HelperObservation>();
        foreach (var fn in functions)
            CollectObservations(fn, observations);
        return InferNullReferenceRaiser(observations);
    }

    public static IReadOnlyDictionary<ulong, RuntimeHelper> InferNullReferenceRaiser(IEnumerable<HelperObservation> observations)
    {
        var total = new Dictionary<ulong, int>();
        var matching = new Dictionary<ulong, int>();
        foreach (var observation in observations)
        {
            total[observation.Target] = total.GetValueOrDefault(observation.Target) + 1;
            if (observation.NullCheckShape)
                matching[observation.Target] = matching.GetValueOrDefault(observation.Target) + 1;
        }
        var result = new Dictionary<ulong, RuntimeHelper>();
        foreach (var (target, count) in matching)
        {
            var all = total[target];
            if (count >= MinimumSites && count >= MinimumShare * all)
            {
                result[target] = new RuntimeHelper(RuntimeHelperKind.RaiseNullReferenceException,
                    $"identified from call-site structure: {count} of {all} noreturn call sites are reached only when a register is zero, pass no arguments, and the other path dereferences that register");
            }
        }
        return result.Count == 1 ? result : new Dictionary<ulong, RuntimeHelper>();
    }

    /// <summary>Records compact noreturn-call evidence and drops the decoded instruction map.</summary>
    public static void CollectObservations(DiscoveredFunction fn, ICollection<HelperObservation> into)
    {
        var branchTargets = new HashSet<ulong>();
        foreach (var op in fn.Instructions.Values)
        {
            if ((op.IsConditionalBranch || op.Kind == A64Kind.B) && op.Target != 0)
                branchTargets.Add(op.Target);
        }
        foreach (var (site, _) in fn.NoReturnCalls)
        {
            if (!fn.Instructions.TryGetValue(site, out var call) || call.Kind != A64Kind.Bl)
                continue;
            into.Add(new HelperObservation(call.Target, IsNullCheckSite(fn, site, branchTargets)));
        }
    }

    private static bool IsNullCheckSite(DiscoveredFunction fn, ulong site, HashSet<ulong> branchTargets)
    {
        var cursor = site;
        while (true)
        {
            var prev = cursor - 4;
            if (!fn.Instructions.TryGetValue(prev, out var op) || branchTargets.Contains(cursor) || op.IsBlockTerminator || op.Kind == A64Kind.Bl)
                break;
            if (!IsFrameSetup(op))
                return false;
            cursor = prev;
        }
        var coldStart = cursor;
        A64Op? guard = null;
        bool Accept(A64Op op)
        {
            if (guard is not null || !op.Is64)
                return false;
            guard = op;
            return true;
        }
        foreach (var op in fn.Instructions.Values)
        {
            if (op.Address + 4 == coldStart)
            {
                if (op.Kind == A64Kind.Cbnz)
                {
                    if (!Accept(op))
                        return false;
                }
                else if (op.IsConditionalBranch || op.Kind == A64Kind.Bl && !fn.NoReturnCalls.ContainsKey(op.Address) || !op.IsBlockTerminator && op.Kind != A64Kind.Bl)
                {
                    return false;
                }
            }
            if ((op.IsConditionalBranch || op.Kind == A64Kind.B) && op.Target == coldStart)
            {
                if (op.Kind != A64Kind.Cbz || !Accept(op))
                    return false;
            }
        }
        if (guard is null)
            return false;
        var register = guard.Rd;
        var other = guard.Kind == A64Kind.Cbz ? guard.Address + 4 : guard.Target;
        for (var i = 0; i < LookAhead; i++)
        {
            if (!fn.Instructions.TryGetValue(other, out var op))
                return false;
            if (op.Kind is A64Kind.Ldr or A64Kind.Str or A64Kind.Ldp or A64Kind.Stp && op.Rn == register)
                return true;
            if (op.IsBlockTerminator || op.Kind is A64Kind.Bl or A64Kind.Blr || Writes(op, register))
                return false;
            other += 4;
        }
        return false;
    }

    private static bool IsFrameSetup(A64Op op) =>
        op.Kind == A64Kind.Nop
        || op.Kind == A64Kind.Stp && op.Rn == 31
        || op.Kind == A64Kind.Add && op.HasImm && op.Rn == 31 && op.Rd == 29
        || op.Kind == A64Kind.Sub && op.HasImm && op.Rn == 31 && op.Rd == 31;

    private static bool Writes(A64Op op, int register) => op.Kind switch
    {
        A64Kind.Str or A64Kind.Stp or A64Kind.Cbz or A64Kind.Cbnz or A64Kind.Tbz or A64Kind.Tbnz or A64Kind.BCond or A64Kind.B or A64Kind.Ret or A64Kind.Nop => false,
        A64Kind.Ldr => op.Rt == register,
        A64Kind.Ldp => op.Rt == register || op.Rt2 == register,
        _ => op.Rd == register
    };
}

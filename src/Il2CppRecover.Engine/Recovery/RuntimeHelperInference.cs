namespace Il2CppRecover.Recovery;

/// <summary>
/// Identifies the IL2CPP null-reference raise helper in a stripped binary from call-site shape.
/// The dominant noreturn callee is labelled when at least <see cref="MinimumSites"/> of its sites
/// are reached only by CBZ/CBNZ on a 64-bit register, pass no arguments, and the other path uses
/// that register, and it has at least ten times as many such sites as any other callee.
/// <see cref="MinimumShare"/> is the bar at which that description covers the callee as a whole.
/// Below it, only a site that itself has the shape is emitted as NullReferenceException.
/// On Unity 2022.3 the use is
/// often a copy into an argument register and a call or tail call after the frame epilogue, and
/// several checks share one outlined call. A branch into that call that is not a 64-bit CBZ or
/// CBNZ leaves the site unlabelled. Two helpers of similar size are left unlabelled; a helper
/// with at least ten times as many matching sites is kept. A call is emitted as
/// NullReferenceException only when that call site itself matches.
/// </summary>
public readonly record struct HelperObservation(ulong Target, bool NullCheckShape);

public static class RuntimeHelperInference
{
    public const int MinimumSites = 3;
    public const double MinimumShare = 0.9;
    /// <summary>
    /// Instructions examined on the non-zero path. Eight was enough for an immediate dereference.
    /// Unity 2022.3 copies the register, runs a frame epilogue, and tail-calls, which sits past
    /// that window (measured past 12 instructions on String and collator methods).
    /// </summary>
    private const int OtherPathBudget = 64;

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
        var ranked = matching
            .Select(pair => (Target: pair.Key, Count: pair.Value, All: total[pair.Key]))
            .Where(item => item.Count >= MinimumSites)
            .OrderByDescending(item => item.Count)
            .ToList();
        if (ranked.Count == 0)
            return new Dictionary<ulong, RuntimeHelper>();
        var best = ranked[0];
        if (ranked.Skip(1).Any(item => best.Count < item.Count * 10))
            return new Dictionary<ulong, RuntimeHelper>();
        var exclusive = best.Count >= MinimumShare * best.All;
        var evidence = exclusive
            ? $"identified from call-site structure: {best.Count} of {best.All} noreturn call sites are reached only when a 64-bit register is zero, pass no arguments, and the other path uses that register"
            : $"dominant noreturn callee by call-site structure: {best.Count} of {best.All} noreturn sites match a null check. Share is below {MinimumShare:P0}, so only a site with that shape is labelled; other calls stay unidentified";
        return new Dictionary<ulong, RuntimeHelper>
        {
            [best.Target] = new RuntimeHelper(RuntimeHelperKind.RaiseNullReferenceException, evidence)
        };
    }

    public static bool IsNullCheck(DiscoveredFunction fn, ulong site)
    {
        var branchTargets = new HashSet<ulong>();
        foreach (var op in fn.Instructions.Values)
        {
            if ((op.IsConditionalBranch || op.Kind == A64Kind.B) && op.Target != 0)
                branchTargets.Add(op.Target);
        }
        return IsNullCheckSite(fn, site, branchTargets);
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
        var guards = new List<A64Op>();
        foreach (var op in fn.Instructions.Values)
        {
            if (op.Address + 4 == coldStart)
            {
                if (op.Kind == A64Kind.Cbnz && op.Is64)
                    guards.Add(op);
                else if (op.IsConditionalBranch || op.Kind == A64Kind.Bl && !fn.NoReturnCalls.ContainsKey(op.Address) || !op.IsBlockTerminator && op.Kind != A64Kind.Bl)
                    return false;
            }
            if ((op.IsConditionalBranch || op.Kind == A64Kind.B) && op.Target == coldStart)
            {
                if (op.Kind is not (A64Kind.Cbz or A64Kind.Cbnz) || !op.Is64)
                    return false;
                if (op.Kind != A64Kind.Cbnz || op.Address + 4 != coldStart)
                    guards.Add(op);
            }
        }
        if (guards.Count == 0)
            return false;
        foreach (var guard in guards)
        {
            var other = guard.Kind == A64Kind.Cbnz && guard.Address + 4 == coldStart ? guard.Target : guard.Address + 4;
            if (!OtherPathUses(fn, other, guard.Rd))
                return false;
        }
        return true;
    }

    private static bool OtherPathUses(DiscoveredFunction fn, ulong start, int register)
    {
        var queue = new Queue<(ulong Address, ulong Live)>();
        var seen = new HashSet<ulong>();
        queue.Enqueue((start, 1UL << register));
        var budget = OtherPathBudget;
        while (queue.Count > 0 && budget-- > 0)
        {
            var (address, live) = queue.Dequeue();
            if (live == 0 || !seen.Add(address) || !fn.Instructions.TryGetValue(address, out var op))
                continue;
            if (op.Kind == A64Kind.Unknown)
                continue;
            if (Uses(op, live))
                return true;
            if (op.Kind is A64Kind.Bl or A64Kind.Blr)
            {
                if (ArgumentLive(live))
                    return true;
                live &= ~((1UL << 19) - 1);
                queue.Enqueue((address + 4, live));
                continue;
            }
            if (op.Kind == A64Kind.B)
            {
                if (!fn.Instructions.ContainsKey(op.Target))
                    return ArgumentLive(live);
                queue.Enqueue((op.Target, Transfer(op, live)));
                continue;
            }
            if (op.Kind is A64Kind.Ret or A64Kind.Br or A64Kind.Trap)
                continue;
            var next = Transfer(op, live);
            if (op.IsConditionalBranch)
            {
                if (fn.Instructions.ContainsKey(address + 4))
                    queue.Enqueue((address + 4, next));
                if (fn.Instructions.ContainsKey(op.Target))
                    queue.Enqueue((op.Target, next));
                continue;
            }
            queue.Enqueue((address + 4, next));
        }
        return false;
    }

    private static bool Uses(A64Op op, ulong live)
    {
        if (op.Kind is A64Kind.Ldr or A64Kind.Str or A64Kind.Ldp or A64Kind.Stp && op.Rn != 31 && Live(live, op.Rn))
            return true;
        if (op.Kind is A64Kind.Str or A64Kind.Stp && (Live(live, op.Rt) || op.Kind == A64Kind.Stp && Live(live, op.Rt2)))
            return true;
        if (op.Kind is A64Kind.Add or A64Kind.Sub)
        {
            if (op.Rn != 31 && Live(live, op.Rn))
                return true;
            if (!op.HasImm && op.Rm != 31 && Live(live, op.Rm))
                return true;
        }
        return false;
    }

    private static bool ArgumentLive(ulong live)
    {
        for (var register = 0; register <= 7; register++)
        {
            if (Live(live, register))
                return true;
        }
        return false;
    }

    private static ulong Transfer(A64Op op, ulong live)
    {
        var copyTo = -1;
        if (op.Kind == A64Kind.Orr && op.Rn == 31 && !op.InvertRm && op.ShiftAmount == 0 && Live(live, op.Rm))
            copyTo = op.Rd;
        foreach (var dest in Destinations(op))
        {
            if (dest == copyTo)
                live |= 1UL << dest;
            else if (dest < 64)
                live &= ~(1UL << dest);
        }
        return live;
    }

    private static IEnumerable<int> Destinations(A64Op op)
    {
        switch (op.Kind)
        {
            case A64Kind.Ldr:
                if (op.Vector)
                    yield break;
                yield return op.Rt;
                yield break;
            case A64Kind.Ldp:
                if (op.Vector)
                    yield break;
                yield return op.Rt;
                yield return op.Rt2;
                yield break;
            case A64Kind.FmovToGeneral:
            case A64Kind.Fcvtzs:
            case A64Kind.Fcvtzu:
                yield return op.Rd;
                yield break;
            case A64Kind.FmovReg:
            case A64Kind.FmovImm:
            case A64Kind.FmovFromGeneral:
            case A64Kind.Fadd:
            case A64Kind.Fsub:
            case A64Kind.Fmul:
            case A64Kind.Fdiv:
            case A64Kind.Fneg:
            case A64Kind.Fabs:
            case A64Kind.Fcmp:
            case A64Kind.Fcsel:
            case A64Kind.Scvtf:
            case A64Kind.Ucvtf:
            case A64Kind.FcvtToDouble:
            case A64Kind.FcvtToSingle:
            case A64Kind.Fsqrt:
            case A64Kind.Fmin:
            case A64Kind.Fmax:
            case A64Kind.FminNum:
            case A64Kind.FmaxNum:
            case A64Kind.SimdScvtf:
            case A64Kind.SimdUcvtf:
            case A64Kind.VectorMov:
            case A64Kind.SimdOrr:
            case A64Kind.Str:
            case A64Kind.Stp:
            case A64Kind.Cbz:
            case A64Kind.Cbnz:
            case A64Kind.Tbz:
            case A64Kind.Tbnz:
            case A64Kind.BCond:
            case A64Kind.B:
            case A64Kind.Bl:
            case A64Kind.Blr:
            case A64Kind.Ret:
            case A64Kind.Nop:
            case A64Kind.Unknown:
                yield break;
            default:
                yield return op.Rd;
                yield break;
        }
    }

    private static bool Live(ulong live, int register) => register is >= 0 and < 64 && (live & (1UL << register)) != 0;

    private static bool IsFrameSetup(A64Op op) =>
        op.Kind == A64Kind.Nop
        || op.Kind == A64Kind.Stp && op.Rn == 31
        || op.Kind == A64Kind.Add && op.HasImm && op.Rn == 31 && op.Rd == 29
        || op.Kind == A64Kind.Sub && op.HasImm && op.Rn == 31 && op.Rd == 31;
}

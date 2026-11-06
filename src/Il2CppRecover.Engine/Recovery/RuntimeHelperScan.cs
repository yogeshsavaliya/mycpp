namespace Il2CppRecover.Recovery;

/// <summary>
/// Finds the null-reference helper without retaining every decoded function.
/// The second pass rediscovers only callers of newly marked noreturn targets.
/// </summary>
public static class RuntimeHelperScan
{
    public sealed class Result
    {
        public IReadOnlyDictionary<ulong, RuntimeHelper> Helpers { get; set; } = new Dictionary<ulong, RuntimeHelper>();
        public int Pass1Functions { get; set; }
        public int Pass2Functions { get; set; }
        public List<string> Notes { get; } = [];
    }

    public static Result Scan(CodeSpace space, IReadOnlyList<ulong> entries, CancellationToken cancellationToken, Action<int, int, string>? progress = null)
    {
        var result = new Result();
        var byCaller = new Dictionary<ulong, List<HelperObservation>>();
        var callersOf = new Dictionary<ulong, List<ulong>>();
        var marked = new List<ulong>();
        var total = entries.Count;
        var index = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            if ((index & 31) == 0 || index == total)
                progress?.Invoke(index, total, "pass 1");
            if (!TryDiscover(space, entry, cancellationToken, result, out var fn))
                continue;
            result.Pass1Functions++;
            Remember(entry, fn, byCaller, callersOf, space, result, marked);
        }

        var queue = new Queue<ulong>();
        var inQueue = new HashSet<ulong>();
        foreach (var target in marked)
            EnqueueCallers(target, callersOf, queue, inQueue);
        var scanned = new HashSet<ulong>();
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = queue.Dequeue();
            inQueue.Remove(entry);
            if (!scanned.Add(entry))
                continue;
            result.Pass2Functions++;
            if ((result.Pass2Functions & 31) == 0)
                progress?.Invoke(result.Pass2Functions, queue.Count + result.Pass2Functions, "pass 2");
            if (!TryDiscover(space, entry, cancellationToken, result, out var fn))
                continue;
            var added = new List<ulong>();
            Remember(entry, fn, byCaller, callersOf, space, result, added);
            if (added.Count > 0)
            {
                scanned.Clear();
                scanned.Add(entry);
                foreach (var target in added)
                    EnqueueCallers(target, callersOf, queue, inQueue);
            }
        }

        var observations = new List<HelperObservation>();
        foreach (var list in byCaller.Values)
            observations.AddRange(list);
        result.Helpers = RuntimeHelperInference.InferNullReferenceRaiser(observations);
        return result;
    }

    private static bool TryDiscover(CodeSpace space, ulong entry, CancellationToken cancellationToken, Result result, out DiscoveredFunction fn)
    {
        try
        {
            fn = FunctionDiscovery.Discover(space, entry, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            result.Notes.Add($"0x{entry:X}: runtime-helper discovery failed: {ex.GetType().Name}: {ex.Message}");
            fn = null!;
            return false;
        }
    }

    private static void Remember(ulong entry, DiscoveredFunction fn, Dictionary<ulong, List<HelperObservation>> byCaller, Dictionary<ulong, List<ulong>> callersOf, CodeSpace space, Result result, List<ulong> marked)
    {
        foreach (var op in fn.Instructions.Values)
        {
            if (op.Kind != A64Kind.Bl)
                continue;
            if (!callersOf.TryGetValue(op.Target, out var callers))
            {
                callers = [];
                callersOf[op.Target] = callers;
            }
            if (!callers.Contains(entry))
                callers.Add(entry);
        }
        var fresh = new List<string>();
        foreach (var (site, evidence) in fn.NoReturnCalls)
        {
            if (fn.Instructions.TryGetValue(site, out var call) && evidence.StartsWith("call at", StringComparison.Ordinal))
            {
                if (space.AddNoReturn(call.Target, "elsewhere in the image, " + evidence))
                {
                    marked.Add(call.Target);
                    fresh.Add($"0x{call.Target:X} marked noreturn: {evidence}.");
                }
            }
        }
        if (fresh.Count > 0 && result.Notes.Count < 32)
            result.Notes.AddRange(fresh.Take(8));
        var observations = new List<HelperObservation>();
        RuntimeHelperInference.CollectObservations(fn, observations);
        byCaller[entry] = observations;
    }

    private static void EnqueueCallers(ulong target, Dictionary<ulong, List<ulong>> callersOf, Queue<ulong> queue, HashSet<ulong> queued)
    {
        if (!callersOf.TryGetValue(target, out var callers))
            return;
        foreach (var caller in callers)
        {
            if (queued.Add(caller))
                queue.Enqueue(caller);
        }
    }
}

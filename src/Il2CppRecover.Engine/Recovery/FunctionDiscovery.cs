using Il2CppRecover.Binary;

namespace Il2CppRecover.Recovery;

/// <summary>
/// What the lifter knows about the native image beyond one method's bytes:
/// readable executable ranges, known function entries, and functions known not to return.
/// </summary>
public sealed class CodeSpace
{
    private readonly Func<ulong, uint?> _readWord;
    private readonly Func<ulong, bool> _isExecutable;
    private readonly ulong[] _entries;
    private readonly Dictionary<ulong, string> _noReturn;

    /// <summary>An address code may not cross even when no further method entry is known. It is not a method entry.</summary>
    public ulong? Limit { get; init; }

    public CodeSpace(Func<ulong, uint?> readWord, Func<ulong, bool> isExecutable, IEnumerable<ulong> entries, IReadOnlyDictionary<ulong, string>? noReturn = null)
    {
        _readWord = readWord;
        _isExecutable = isExecutable;
        _entries = entries.Distinct().Order().ToArray();
        _noReturn = noReturn is null ? [] : new Dictionary<ulong, string>(noReturn);
    }

    public static CodeSpace ForSlice(byte[] code, ulong address)
    {
        var end = address + (ulong)code.Length;
        return new CodeSpace(
            a => a >= address && a + 4 <= end && (a - address) % 4 == 0 ? BitConverter.ToUInt32(code, (int)(a - address)) : null,
            a => a >= address && a < end,
            [address])
        { Limit = end };
    }

    public static CodeSpace ForElf(ElfImage elf, byte[] image, IEnumerable<ulong> entries, IReadOnlyDictionary<ulong, string>? noReturn = null)
    {
        var exec = elf.Segments.Where(s => s.Type == 1 && (s.Flags & 1) != 0).ToList();
        bool IsExec(ulong a) => exec.Any(s => a >= s.VirtualAddress && a - s.VirtualAddress < s.FileSize);
        uint? Read(ulong a)
        {
            if (!IsExec(a) || !elf.TryMap(a, out var offset) || offset < 0 || offset + 4 > image.Length)
                return null;
            return BitConverter.ToUInt32(image, (int)offset);
        }
        return new CodeSpace(Read, IsExec, entries, noReturn);
    }

    public uint? ReadWord(ulong address) => _readWord(address);
    public bool IsExecutable(ulong address) => _isExecutable(address);
    public bool IsEntry(ulong address) => Array.BinarySearch(_entries, address) >= 0;
    public IReadOnlyList<ulong> Entries => _entries;

    public ulong? NextEntryAfter(ulong address)
    {
        var index = Array.BinarySearch(_entries, address);
        index = index >= 0 ? index + 1 : ~index;
        ulong? next = index < _entries.Length ? _entries[index] : null;
        if (Limit is ulong limit && limit > address && (next is null || limit < next))
            return limit;
        return next;
    }

    public ulong? EntryContaining(ulong address)
    {
        var index = Array.BinarySearch(_entries, address);
        if (index >= 0)
            return _entries[index];
        index = ~index - 1;
        return index >= 0 ? _entries[index] : null;
    }

    public bool TryGetNoReturn(ulong address, out string evidence)
    {
        if (_noReturn.TryGetValue(address, out evidence!))
            return true;
        if (ReadWord(address) is uint word && A64Decoder.Decode(word, address).Kind == A64Kind.Trap)
        {
            evidence = "the callee's first instruction is a trap (BRK/UDF/HLT)";
            return true;
        }
        evidence = "";
        return false;
    }

    public bool AddNoReturn(ulong address, string evidence) => _noReturn.TryAdd(address, evidence);
    public IReadOnlyDictionary<ulong, string> NoReturn => _noReturn;
}

public sealed class DiscoveredFunction
{
    public ulong Entry { get; init; }
    public ulong? UpperBound { get; init; }
    public SortedDictionary<ulong, A64Op> Instructions { get; } = [];
    public List<string> Issues { get; } = [];
    public List<string> Evidence { get; } = [];
    public HashSet<ulong> TailCalls { get; } = [];
    public Dictionary<ulong, string> NoReturnCalls { get; } = [];
    public List<(ulong From, ulong To)> Edges { get; } = [];
    public bool HasBackEdgeCandidate { get; set; }
    public bool ControlFlowClosed => Issues.Count == 0;
    private readonly HashSet<(ulong From, ulong To)> _edgeSet = [];

    public void NoteEdge(ulong from, ulong to)
    {
        if (_edgeSet.Add((from, to)))
            Edges.Add((from, to));
    }
}

/// <summary>
/// Recursive descent from one entry. The next known entry is only an upper bound.
/// A transfer that leaves the range is classified; it is not treated as the method end.
/// </summary>
public static class FunctionDiscovery
{
    public const int MaxInstructions = 16384;

    public static DiscoveredFunction Discover(CodeSpace space, ulong entry, CancellationToken cancellationToken = default, int? maxInstructions = null, Action? pulse = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = maxInstructions ?? MaxInstructions;
        if (limit <= 0)
            limit = MaxInstructions;
        var upper = space.NextEntryAfter(entry);
        var fn = new DiscoveredFunction { Entry = entry, UpperBound = upper };
        bool Inside(ulong a) => a >= entry && (upper is null || a < upper.Value);
        var work = new Stack<ulong>();
        work.Push(entry);
        var steps = 0;
        while (work.Count > 0)
        {
            var address = work.Pop();
            while (true)
            {
                if ((++steps & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pulse?.Invoke();
                }
                if (fn.Instructions.TryGetValue(address, out var known))
                {
                    if (space.ReadWord(address) is not uint again)
                        fn.Issues.Add($"0x{address:X}: repeated visit could not read the instruction stored as word 0x{known.Word:X8}.");
                    else if (again != known.Word)
                        fn.Issues.Add($"0x{address:X}: repeated visit decoded word 0x{again:X8}, stored word 0x{known.Word:X8}. The block was not overwritten.");
                    break;
                }
                if (fn.Instructions.Count >= limit)
                {
                    fn.Issues.Add(limit == MaxInstructions
                        ? $"Discovery stopped after {MaxInstructions} instructions."
                        : $"AnalysisBudgetExceeded: discovery stopped after {limit} instructions.");
                    return fn;
                }
                if (!Inside(address))
                {
                    fn.Issues.Add($"0x{address:X}: execution falls through past the next known method entry 0x{upper:X}. The method end was not guessed.");
                    break;
                }
                if (space.ReadWord(address) is not uint word)
                {
                    fn.Issues.Add($"0x{address:X}: not readable executable code.");
                    break;
                }
                var op = A64Decoder.Decode(word, address);
                fn.Instructions[address] = op;
                var next = address + 4;
                switch (op.Kind)
                {
                    case A64Kind.Ret:
                    case A64Kind.Trap:
                        goto endPath;
                    case A64Kind.Br:
                        fn.Issues.Add($"0x{address:X}: indirect branch (BR x{op.Rn}). Jump tables and computed targets are not resolved.");
                        goto endPath;
                    case A64Kind.B:
                        if (Inside(op.Target) && op.Target != entry)
                        {
                            if (op.Target <= address)
                                fn.HasBackEdgeCandidate = true;
                            fn.NoteEdge(address, op.Target);
                            address = op.Target;
                            continue;
                        }
                        ClassifyUnconditional(space, fn, op);
                        goto endPath;
                    case A64Kind.BCond:
                    case A64Kind.Cbz:
                    case A64Kind.Cbnz:
                    case A64Kind.Tbz:
                    case A64Kind.Tbnz:
                        if (Inside(op.Target))
                        {
                            if (op.Target <= address)
                                fn.HasBackEdgeCandidate = true;
                            fn.NoteEdge(address, op.Target);
                            work.Push(op.Target);
                        }
                        else
                        {
                            fn.Issues.Add($"0x{address:X}: conditional branch target 0x{op.Target:X} is outside the slice ({Describe(space, entry, op.Target)}). Conditional tail calls are not assumed.");
                        }
                        if (Inside(next))
                            fn.NoteEdge(address, next);
                        address = next;
                        continue;
                    case A64Kind.Bl:
                        if (space.TryGetNoReturn(op.Target, out var why))
                        {
                            fn.NoReturnCalls[address] = why;
                            goto endPath;
                        }
                        if (upper is not null && next == upper.Value && space.IsEntry(upper.Value))
                        {
                            var evidence = $"call at 0x{address:X} is the last instruction before the next method entry 0x{upper:X}";
                            fn.NoReturnCalls[address] = evidence;
                            fn.Evidence.Add($"0x{op.Target:X} treated as noreturn: {evidence}.");
                            goto endPath;
                        }
                        if (Inside(next))
                            fn.NoteEdge(address, next);
                        address = next;
                        continue;
                    default:
                        if (Inside(next))
                            fn.NoteEdge(address, next);
                        address = next;
                        continue;
                }
            }
            endPath:;
        }
        return fn;
    }

    private static void ClassifyUnconditional(CodeSpace space, DiscoveredFunction fn, A64Op op)
    {
        var target = op.Target;
        if (target == fn.Entry)
        {
            fn.Issues.Add($"0x{op.Address:X}: branch back to the method entry. Self tail-recursion is not lifted.");
            return;
        }
        if (space.IsEntry(target))
        {
            fn.TailCalls.Add(op.Address);
            return;
        }
        fn.Issues.Add($"0x{op.Address:X}: unconditional branch to 0x{target:X} ({Describe(space, fn.Entry, target)}). Not treated as internal and not treated as a tail call.");
    }

    public static string Describe(CodeSpace space, ulong entry, ulong target)
    {
        if (!space.IsExecutable(target))
            return "not in an executable segment of the loaded image";
        if (space.IsEntry(target))
            return "a known method entry";
        var owner = space.EntryContaining(target);
        if (owner is null)
            return "executable, before the first known method entry: runtime or unmapped code";
        if (owner.Value == entry)
            return "inside this method's range but not reached as an internal block";
        return $"inside the range that starts at method entry 0x{owner.Value:X}, +0x{target - owner.Value:X}: shared code, a cold block placed after another method, or an incorrect boundary";
    }

    public static ThunkResolution ResolveThunk(CodeSpace space, ulong address, Func<ulong, bool> isResolved, int maxHops = 8)
    {
        var chain = new List<ulong> { address };
        var seen = new HashSet<ulong> { address };
        var current = address;
        for (var hop = 0; hop <= maxHops; hop++)
        {
            if (isResolved(current) && hop > 0)
                return new ThunkResolution(true, current, chain, "");
            if (space.ReadWord(current) is not uint word)
                return new ThunkResolution(false, null, chain, $"0x{current:X} is not readable executable code.");
            var op = A64Decoder.Decode(word, current);
            if (op.Kind != A64Kind.B)
                return new ThunkResolution(false, null, chain, hop == 0 ? "not a direct thunk" : $"0x{current:X} is not a known method and is not a single-branch thunk.");
            if (!space.IsExecutable(op.Target))
                return new ThunkResolution(false, null, chain, $"thunk target 0x{op.Target:X} is not executable.");
            if (!seen.Add(op.Target))
                return new ThunkResolution(false, null, chain, $"thunk chain cycles back to 0x{op.Target:X}.");
            chain.Add(op.Target);
            current = op.Target;
        }
        return new ThunkResolution(false, null, chain, $"thunk chain exceeds {maxHops} hops.");
    }
}

public sealed record ThunkResolution(bool Resolved, ulong? Target, IReadOnlyList<ulong> Chain, string Problem)
{
    public string Describe() => string.Join(" -> ", Chain.Select(a => "0x" + a.ToString("X")));
}

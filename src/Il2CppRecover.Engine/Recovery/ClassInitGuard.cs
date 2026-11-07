namespace Il2CppRecover.Recovery;

/// <summary>
/// Recognises <c>ldr w, [klass, #cctor_finished]; cbnz/cbz; bl runtime_class_init</c> when the
/// klass register is a resolved Type metadata value. The check is rewritten to a straight edge
/// and the continuation records a class-init statement. A static use of that same type on the
/// next statement drops the statement, because C# runs the initializer itself.
/// </summary>
internal static class ClassInitGuard
{
    private enum Tag { None, Address, Entry, Value, Flag }

    private readonly record struct Abs(Tag Tag, ulong Address, MetadataSlot? Slot);

    public static void Elide(List<StructuredLifter.Block> blocks, Dictionary<ulong, StructuredLifter.Block> blockAt, Func<ulong, MetadataSlot?>? resolve, int staticOffset, int cctorOffset, List<string> evidence)
    {
        if (resolve is null || cctorOffset == 0)
            return;
        var incoming = Dataflow(blocks, resolve, cctorOffset);
        var predecessors = new Dictionary<ulong, int>();
        foreach (var block in blocks)
        {
            foreach (var successor in block.Succ)
                predecessors[successor] = predecessors.GetValueOrDefault(successor) + 1;
        }
        foreach (var block in blocks)
        {
            var term = block.Term;
            if (block.Kind != StructuredLifter.TermKind.Cond || term is null || term.Kind is not (A64Kind.Cbnz or A64Kind.Cbz) || term.Is64)
                continue;
            if (!incoming.TryGetValue(block.Start, out var state))
                continue;
            var regs = state.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var op in block.Body)
                Transfer(op, regs, resolve, cctorOffset);
            if (!regs.TryGetValue(term.Rd, out var tested) || tested.Tag != Tag.Flag || tested.Address != (ulong)cctorOffset || tested.Slot is not { Kind: MetadataSlotKind.Type } flag)
                continue;
            var initialized = term.Kind == A64Kind.Cbnz ? block.Taken : block.Next;
            var coldAddress = term.Kind == A64Kind.Cbnz ? block.Next : block.Taken;
            if (!blockAt.TryGetValue(coldAddress, out var cold) || predecessors.GetValueOrDefault(coldAddress) != 1)
                continue;
            if (cold.Kind is not (StructuredLifter.TermKind.Fall or StructuredLifter.TermKind.Jump))
                continue;
            if (!IsInitColdBody(cold.Body, regs, staticOffset, flag))
                continue;
            StructuredLifter.Block? reload = null;
            var callOnly = cold.Body.Count == 1;
            StructuredLifter.Block join;
            if (cold.Next == initialized)
            {
                if (!blockAt.TryGetValue(initialized, out var direct) || predecessors.GetValueOrDefault(initialized) != 2)
                    continue;
                join = direct;
            }
            else if (callOnly
                && blockAt.TryGetValue(cold.Next, out reload)
                && predecessors.GetValueOrDefault(cold.Next) == 1
                && reload.Body.Count == 1
                && reload.Kind is StructuredLifter.TermKind.Fall or StructuredLifter.TermKind.Jump
                && reload.Next == initialized
                && blockAt.TryGetValue(initialized, out var afterReload)
                && predecessors.GetValueOrDefault(initialized) == 2
                && IsKlassReload(reload.Body[0], regs))
            {
                join = afterReload;
            }
            else
                continue;
            for (var i = block.Body.Count - 1; i >= 0; i--)
            {
                var load = block.Body[i];
                if (load.Kind == A64Kind.Ldr && load.Rt == term.Rd && load.Size == 4 && load.Imm == cctorOffset)
                {
                    block.Body.RemoveAt(i);
                    break;
                }
            }
            block.Kind = StructuredLifter.TermKind.Jump;
            block.Term = null;
            block.Next = initialized;
            block.Taken = 0;
            block.Succ.Clear();
            block.Succ.Add(initialized);
            cold.Succ.Clear();
            cold.Next = 0;
            cold.Taken = 0;
            cold.Kind = StructuredLifter.TermKind.Trap;
            if (reload is not null)
            {
                reload.Succ.Clear();
                reload.Next = 0;
                reload.Taken = 0;
                reload.Kind = StructuredLifter.TermKind.Trap;
            }
            join.ClassInits.Add(flag);
            evidence.Add($"0x{term.Address:X}: runtime class init of {flag.Display}. The cctor_finished word at klass+0x{cctorOffset:X} and the following init call were replaced with an explicit class initializer. No field was invented for the word.");
        }
    }

    private static bool IsInitColdBody(List<A64Op> body, Dictionary<int, Abs> regs, int staticOffset, MetadataSlot flag)
    {
        if (body.Count == 0)
            return false;
        // The klass may already sit in another register: mov x0, Xn; bl; reload.
        var start = 0;
        if (body[0].Kind == A64Kind.Orr && body[0].Is64 && body[0].Rd == 0 && body[0].Rn == 31 && !body[0].InvertRm && body[0].ShiftAmount == 0 && body[0].Rm != 31
            && regs.TryGetValue(body[0].Rm, out var source) && source.Tag == Tag.Value && source.Slot == flag)
            start = 1;
        if (body.Count <= start || body[start].Kind != A64Kind.Bl)
            return false;
        var local = new Dictionary<int, Abs>(regs);
        for (var i = start + 1; i < body.Count; i++)
        {
            var op = body[i];
            if (op.Kind != A64Kind.Ldr || op.Vector || op.SignedLoad || op.Index != A64Index.Offset || !local.TryGetValue(op.Rn, out var baseReg) || baseReg.Slot != flag)
                return false;
            if (op.Size == 8 && op.Imm == 0 && baseReg.Tag == Tag.Entry)
            {
                local[op.Rt] = new Abs(Tag.Value, 0, flag);
                continue;
            }
            if (op.Size == 8 && op.Imm == staticOffset && baseReg.Tag == Tag.Value)
            {
                local[op.Rt] = new Abs(Tag.None, 0, flag);
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool IsKlassReload(A64Op op, Dictionary<int, Abs> regs) =>
        op.Kind == A64Kind.Ldr
        && op.Size == 8
        && !op.SignedLoad
        && !op.Vector
        && op.Index == A64Index.Offset
        && op.Imm == 0
        && regs.TryGetValue(op.Rn, out var entry)
        && entry.Tag == Tag.Entry
        && entry.Slot is { Kind: MetadataSlotKind.Type };

    private static Dictionary<ulong, Dictionary<int, Abs>> Dataflow(List<StructuredLifter.Block> blocks, Func<ulong, MetadataSlot?> resolve, int cctorOffset)
    {
        var incoming = new Dictionary<ulong, Dictionary<int, Abs>>();
        var outgoing = new Dictionary<ulong, Dictionary<int, Abs>>();
        for (var pass = 0; pass < blocks.Count; pass++)
        {
            var changed = false;
            foreach (var block in blocks)
            {
                var merged = new Dictionary<int, Abs>();
                var seenPred = false;
                foreach (var pred in blocks.Where(candidate => candidate.Succ.Contains(block.Start)))
                {
                    if (!outgoing.TryGetValue(pred.Start, out var outState))
                        continue;
                    if (!seenPred)
                    {
                        foreach (var pair in outState)
                            merged[pair.Key] = pair.Value;
                        seenPred = true;
                        continue;
                    }
                    foreach (var key in merged.Keys.ToList())
                    {
                        if (!outState.TryGetValue(key, out var other) || other != merged[key])
                            merged.Remove(key);
                    }
                }
                var regs = new Dictionary<int, Abs>(merged);
                foreach (var op in block.Body)
                    Transfer(op, regs, resolve, cctorOffset);
                if (block.Term is { } term)
                    Transfer(term, regs, resolve, cctorOffset);
                if (!outgoing.TryGetValue(block.Start, out var previous) || !Same(previous, regs))
                {
                    outgoing[block.Start] = regs;
                    incoming[block.Start] = merged;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }
        return incoming;
    }

    private static bool Same(Dictionary<int, Abs> left, Dictionary<int, Abs> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var other) || other != pair.Value)
                return false;
        }
        return true;
    }

    private static void Transfer(A64Op op, Dictionary<int, Abs> regs, Func<ulong, MetadataSlot?> resolve, int cctorOffset)
    {
        if (op.Kind == A64Kind.Adrp)
        {
            regs[op.Rd] = new Abs(Tag.Address, op.Target, null);
            return;
        }
        if (op.Kind == A64Kind.Ldr && op.Size == 8 && !op.SignedLoad && op.Index == A64Index.Offset && regs.TryGetValue(op.Rn, out var baseReg))
        {
            if (baseReg.Tag == Tag.Address && resolve(baseReg.Address + (ulong)op.Imm) is { } slot)
            {
                regs[op.Rt] = new Abs(slot.ValueAtAddress ? Tag.Value : Tag.Entry, 0, slot);
                return;
            }
            if (baseReg.Tag == Tag.Entry && op.Imm == 0 && baseReg.Slot is not null)
            {
                regs[op.Rt] = new Abs(Tag.Value, 0, baseReg.Slot);
                return;
            }
        }
        if (op.Kind == A64Kind.Ldr && op.Size == 4 && !op.Vector && op.Imm == cctorOffset && regs.TryGetValue(op.Rn, out var klass) && klass.Tag == Tag.Value && klass.Slot is { Kind: MetadataSlotKind.Type })
        {
            regs[op.Rt] = new Abs(Tag.Flag, (ulong)op.Imm, klass.Slot);
            return;
        }
        if (op.Kind is A64Kind.Ldr && !op.Vector)
            regs[op.Rt] = default;
        else if (op.Kind is A64Kind.Ldp && !op.Vector)
        {
            regs[op.Rt] = default;
            regs[op.Rt2] = default;
        }
        else if (A64Effects.WritesGpr(op, op.Rd) && op.Rd is >= 0 and <= 30)
            regs[op.Rd] = default;
        if (op.Kind is A64Kind.Bl or A64Kind.Blr)
        {
            for (var r = 0; r <= 18; r++)
                regs[r] = default;
        }
    }
}

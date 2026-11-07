namespace Il2CppRecover.Recovery;

/// <summary>
/// Recognises the IL2CPP lazy runtime-metadata prologue used by Unity 2022.3 (metadata v29):
/// a byte load from a global flag, a bit-0 test, and a cold path that is only one or more slot
/// loads into x0, one returning call each, a store of the constant 1, and a byte store back to that flag.
/// The cold path is dropped and the flag load is removed. A cold path with any other instruction,
/// a second predecessor, or a noreturn call is left in place.
/// </summary>
internal static class MetadataInitGuard
{
    public static int Elide(List<StructuredLifter.Block> blocks, Dictionary<ulong, StructuredLifter.Block> blockAt, List<string> evidence)
    {
        var predecessors = new Dictionary<ulong, int>();
        foreach (var block in blocks)
        {
            foreach (var successor in block.Succ)
                predecessors[successor] = predecessors.GetValueOrDefault(successor) + 1;
        }

        var removed = 0;
        foreach (var block in blocks)
        {
            var term = block.Term;
            if (block.Kind != StructuredLifter.TermKind.Cond || term is null)
                continue;
            var bitTest = term.Kind is (A64Kind.Tbnz or A64Kind.Tbz) && term.Imm == 0;
            var wholeByte = term.Kind is (A64Kind.Cbnz or A64Kind.Cbz) && !term.Is64;
            if (!bitTest && !wholeByte)
                continue;
            if (!TryFindGuardLoad(block, term.Rd, out var load, out var baseReg, out var offset))
                continue;
            var initializedWhenSet = term.Kind is A64Kind.Tbnz or A64Kind.Cbnz;
            var initialized = initializedWhenSet ? block.Taken : block.Next;
            var coldAddress = initializedWhenSet ? block.Next : block.Taken;
            if (!blockAt.TryGetValue(coldAddress, out var cold) || predecessors.GetValueOrDefault(coldAddress) != 1)
                continue;
            if (!IsInitCold(cold, initialized, baseReg, offset))
                continue;

            block.Body.Remove(load);
            block.Kind = StructuredLifter.TermKind.Jump;
            block.Term = null;
            block.Next = initialized;
            block.Taken = 0;
            block.Succ.Clear();
            block.Succ.Add(initialized);
            // The cold block's only predecessor was this guard. Leave it in the list, but drop its
            // edge into the initialized code so a later merge does not treat the unreachable path
            // as a second predecessor with empty registers.
            cold.Succ.Clear();
            cold.Next = 0;
            cold.Taken = 0;
            cold.Kind = StructuredLifter.TermKind.Trap;
            removed++;
            var test = bitTest ? "bit-0 test" : "whole-byte test";
            evidence.Add($"0x{term.Address:X}: elided IL2CPP lazy runtime-metadata init. The flag byte, the {test}, the slot loads into x0, their calls, and the store of 1 back to that flag were removed. No metadata entity was invented for the flag.");
        }
        return removed;
    }

    private static bool TryFindGuardLoad(StructuredLifter.Block block, int register, out A64Op load, out int baseReg, out long offset)
    {
        load = null!;
        baseReg = 0;
        offset = 0;
        var found = -1;
        for (var i = block.Body.Count - 1; i >= 0; i--)
        {
            var op = block.Body[i];
            if (!A64Effects.WritesGpr(op, register))
                continue;
            if (op.Kind == A64Kind.Ldr && op.Size == 1 && !op.SignedLoad && op.Index == A64Index.Offset && op.Rt == register)
            {
                found = i;
                load = op;
                break;
            }
            return false;
        }
        if (found < 0)
            return false;
        baseReg = load.Rn;
        offset = load.Imm;
        var adrp = -1;
        for (var i = found - 1; i >= 0; i--)
        {
            var op = block.Body[i];
            if (op.Kind == A64Kind.Adrp && op.Rd == baseReg)
            {
                adrp = i;
                break;
            }
            if (A64Effects.WritesGpr(op, baseReg))
                return false;
        }
        if (adrp < 0)
            return false;
        for (var i = found + 1; i < block.Body.Count; i++)
        {
            if (A64Effects.WritesGpr(block.Body[i], baseReg) || A64Effects.WritesGpr(block.Body[i], register))
                return false;
        }
        return true;
    }

    private static bool IsInitCold(StructuredLifter.Block cold, ulong join, int baseReg, long offset)
    {
        if (cold.Kind == StructuredLifter.TermKind.Fall && cold.Next != join)
            return false;
        if (cold.Kind == StructuredLifter.TermKind.Jump && cold.Next != join)
            return false;
        if (cold.Kind is not (StructuredLifter.TermKind.Fall or StructuredLifter.TermKind.Jump))
            return false;
        var ops = cold.Body;
        if (IsRegisterEntryCold(ops, baseReg, offset))
            return true;
        if (ops.Count < 5 || (ops.Count - 2) % 3 != 0)
            return false;
        var groups = (ops.Count - 2) / 3;
        for (var group = 0; group < groups; group++)
        {
            var adrp = ops[group * 3];
            var ldr = ops[group * 3 + 1];
            var call = ops[group * 3 + 2];
            if (adrp.Kind != A64Kind.Adrp || adrp.Rd == baseReg)
                return false;
            if (ldr.Kind != A64Kind.Ldr || ldr.Size != 8 || ldr.SignedLoad || ldr.Index != A64Index.Offset || ldr.Rn != adrp.Rd || ldr.Rt != 0)
                return false;
            if (call.Kind != A64Kind.Bl)
                return false;
        }
        var one = ops[^2];
        var store = ops[^1];
        if (one.Kind != A64Kind.Movz || one.Imm != 1 || one.ShiftAmount != 0 || one.Rd == baseReg)
            return false;
        return store.Kind == A64Kind.Str
            && store.Size == 1
            && !store.SignedLoad
            && store.Index == A64Index.Offset
            && store.Rn == baseReg
            && store.Rt == one.Rd
            && store.Imm == offset;
    }

    /// <summary>
    /// A later guard for an entry already held in a register:
    /// <c>mov x0, Xn; bl; ldr x0, [Xn]; mov w, #1; strb flag</c>.
    /// The call is the metadata-init helper, so <c>Xn</c> is the usage pointer it receives.
    /// </summary>
    private static bool IsRegisterEntryCold(List<A64Op> ops, int baseReg, long offset)
    {
        if (ops.Count != 5)
            return false;
        var copy = ops[0];
        var call = ops[1];
        var reload = ops[2];
        var one = ops[3];
        var store = ops[4];
        // mov X0, Xn is ORR X0, XZR, Xn.
        if (copy.Kind != A64Kind.Orr || !copy.Is64 || copy.Rd != 0 || copy.Rn != 31 || copy.Rm is 31 || copy.Rm == baseReg || copy.InvertRm || copy.ShiftAmount != 0)
            return false;
        if (call.Kind != A64Kind.Bl)
            return false;
        if (reload.Kind != A64Kind.Ldr || reload.Size != 8 || reload.SignedLoad || reload.Index != A64Index.Offset || reload.Rn != copy.Rm || reload.Rt != 0)
            return false;
        if (one.Kind != A64Kind.Movz || one.Imm != 1 || one.ShiftAmount != 0 || one.Rd == baseReg)
            return false;
        return store.Kind == A64Kind.Str
            && store.Size == 1
            && !store.SignedLoad
            && store.Index == A64Index.Offset
            && store.Rn == baseReg
            && store.Rt == one.Rd
            && store.Imm == offset;
    }
}

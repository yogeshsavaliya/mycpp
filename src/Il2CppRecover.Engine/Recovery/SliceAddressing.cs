namespace Il2CppRecover.Recovery;

/// <summary>
/// Instruction addresses inside one contiguous slice are base + 4 * index.
/// Disarm's Address is not a key: undecodable words are reported as address 0.
/// </summary>
public static class SliceAddressing
{
    public static ulong At(ulong baseAddress, int index) => baseAddress + (ulong)index * 4;

    public static long SignExtend(uint value, int bits)
    {
        var shift = 32 - bits;
        return (int)(value << shift) >> shift;
    }

    public static ulong BranchTarget(ulong pc, uint raw, Arm64MnemonicKind kind)
    {
        var delta = kind switch
        {
            Arm64MnemonicKind.Branch => SignExtend(raw & 0x03FFFFFFu, 26) << 2,
            Arm64MnemonicKind.CompareBranch => SignExtend((raw >> 5) & 0x7FFFFu, 19) << 2,
            Arm64MnemonicKind.TestBranch => SignExtend((raw >> 5) & 0x3FFFu, 14) << 2,
            _ => throw new AddressInvariantException("No PC-relative target for " + kind + " at 0x" + pc.ToString("X") + ".")
        };
        return unchecked(pc + (ulong)delta);
    }

    public static void RequireDistinct(IReadOnlyList<NativeInstruction> listing)
    {
        var seen = new Dictionary<string, NativeInstruction>(listing.Count, StringComparer.Ordinal);
        foreach (var item in listing)
        {
            if (seen.TryGetValue(item.Address, out var first))
            {
                throw new AddressInvariantException(
                    "Duplicate instruction address " + item.Address +
                    ". First record: bytes=" + first.Bytes + " text=" + first.Text +
                    ". Second record: bytes=" + item.Bytes + " text=" + item.Text + ".");
            }
            seen.Add(item.Address, item);
        }
    }
}

public enum Arm64MnemonicKind
{
    Branch,
    CompareBranch,
    TestBranch
}

public sealed class AddressInvariantException(string message) : Exception(message);

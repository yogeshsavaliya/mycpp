namespace Il2CppRecover.Tests;

/// <summary>Encodings from llvm-mc 18 (-triple=aarch64), checked field by field.</summary>
public class A64DecoderTests
{
    private static A64Op D(uint w, ulong at = 0x1000) => A64Decoder.Decode(w, at);

    [Fact]
    public void Logical_shifted_register_keeps_inversion_and_shift()
    {
        var bic = D(0x0AA17C29);
        Assert.Equal(A64Kind.And, bic.Kind);
        Assert.True(bic.InvertRm);
        Assert.Equal(A64Shift.Asr, bic.Shift);
        Assert.Equal(31, bic.ShiftAmount);

        var orr = D(0x2A080528);
        Assert.Equal((A64Kind.Orr, A64Shift.Lsl, 1, false), (orr.Kind, orr.Shift, orr.ShiftAmount, orr.InvertRm));
    }

    [Fact]
    public void Move_wide_scales_the_halfword()
    {
        var movk = D(0xF2E81408);
        Assert.Equal((A64Kind.Movk, 0x40A0L, 48), (movk.Kind, movk.Imm, movk.ShiftAmount));
        Assert.Equal(unchecked((long)0xFFFF0000FFFF0000UL), D(0xB2103FE0).Imm);
    }

    [Fact]
    public void Adrp_page_is_the_instruction_page_plus_the_shifted_immediate()
    {
        Assert.Equal((A64Kind.Adrp, 0x2000UL), (D(0xB0000008).Kind, D(0xB0000008).Target));
    }

    [Fact]
    public void Pair_and_float_encodings()
    {
        var stp = D(0xA9BF7BFD);
        Assert.Equal((A64Kind.Stp, 29, 30, -16L, A64Index.Pre), (stp.Kind, stp.Rt, stp.Rt2, stp.Imm, stp.Index));
        Assert.Equal((A64Kind.FmovImm, 0.5), (D(0x1E2C1000).Kind, D(0x1E2C1000).FloatImm));
        Assert.Equal(A64Kind.Fmul, D(0x1E200820).Kind);
        Assert.Equal(A64Kind.Trap, D(0xD4200020).Kind);
    }
}

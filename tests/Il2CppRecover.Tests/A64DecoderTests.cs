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

    [Fact]
    public void Simd_move_and_float_leaf_encodings_match_llvm_mc_18()
    {
        var mov = D(0x4EA01C08);
        Assert.Equal((A64Kind.VectorMov, 8, 0, 16), (mov.Kind, mov.Rd, mov.Rn, mov.Size));
        Assert.Equal(A64Kind.SimdOrr, D(0x4EA31C41).Kind);
        Assert.Equal((A64Kind.FmovFromGeneral, 0, 31, false), (D(0x1E2703E0).Kind, D(0x1E2703E0).Rd, D(0x1E2703E0).Rn, D(0x1E2703E0).FloatDouble));
        Assert.Equal((A64Kind.FmovFromGeneral, true), (D(0x9E6703E0).Kind, D(0x9E6703E0).FloatDouble));
        Assert.Equal(A64Kind.FmovToGeneral, D(0x9E660000).Kind);
        Assert.Equal((A64Kind.Fsqrt, 0, 1), (D(0x1E21C020).Kind, D(0x1E21C020).Rd, D(0x1E21C020).Rn));
        Assert.Equal((A64Kind.Fmin, 1, 2), (D(0x1E225820).Kind, D(0x1E225820).Rn, D(0x1E225820).Rm));
        Assert.Equal(A64Kind.Fmax, D(0x1E224820).Kind);
        Assert.Equal(A64Kind.FminNum, D(0x1E227820).Kind);
        Assert.Equal(A64Kind.FmaxNum, D(0x1E226820).Kind);
        Assert.Equal((A64Kind.Fcsel, A64Cond.Pl), (D(0x1E225C20).Kind, D(0x1E225C20).Cond));
        Assert.Equal((A64Kind.SimdScvtf, false), (D(0x5E21D820).Kind, D(0x5E21D820).FloatDouble));
        Assert.Equal((A64Kind.SimdUcvtf, true), (D(0x7E61D820).Kind, D(0x7E61D820).FloatDouble));
        var pair = D(0x2D440400);
        Assert.Equal((A64Kind.Ldp, true, 4, 32L, 0, 1), (pair.Kind, pair.Vector, pair.Size, pair.Imm, pair.Rt, pair.Rt2));
        Assert.Equal((A64Kind.Cbnz, false, 8), (D(0x35000048).Kind, D(0x35000048).Is64, D(0x35000048).Rd));
    }
}

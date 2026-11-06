namespace Il2CppRecover.Recovery;

/// <summary>
/// Decodes the AArch64 instructions the structured lifter assigns semantics to,
/// directly from the 32-bit encoding. The pinned Disarm revision drops the N bit
/// of logical shifted-register instructions (so BIC is reported as AND), drops the
/// shift type, and reports MOVK with a 48-bit shift as a 16-bit shift. Listings
/// still come from Disarm. Anything not decoded here is <see cref="A64Kind.Unknown"/>.
/// </summary>
public static class A64Decoder
{
    public static A64Op Decode(uint w, ulong address)
    {
        var op = new A64Op { Word = w, Address = address };

        if ((w & 0x7C000000) == 0x14000000)
        {
            op.Kind = (w & 0x80000000) != 0 ? A64Kind.Bl : A64Kind.B;
            op.Target = unchecked(address + (ulong)(SignExtend(w & 0x03FFFFFF, 26) << 2));
            return op;
        }
        if ((w & 0xFF000010) == 0x54000000)
        {
            op.Kind = A64Kind.BCond;
            op.Cond = (A64Cond)(w & 0xF);
            op.Target = unchecked(address + (ulong)(SignExtend((w >> 5) & 0x7FFFF, 19) << 2));
            return op;
        }
        if ((w & 0x7E000000) == 0x34000000)
        {
            op.Kind = (w & 0x01000000) != 0 ? A64Kind.Cbnz : A64Kind.Cbz;
            op.Is64 = (w & 0x80000000) != 0;
            op.Rd = (int)(w & 0x1F);
            op.Target = unchecked(address + (ulong)(SignExtend((w >> 5) & 0x7FFFF, 19) << 2));
            return op;
        }
        if ((w & 0x7E000000) == 0x36000000)
        {
            op.Kind = (w & 0x01000000) != 0 ? A64Kind.Tbnz : A64Kind.Tbz;
            op.Rd = (int)(w & 0x1F);
            op.Imm = (long)(((w >> 31) << 5) | ((w >> 19) & 0x1F));
            op.Is64 = op.Imm >= 32;
            op.Target = unchecked(address + (ulong)(SignExtend((w >> 5) & 0x3FFF, 14) << 2));
            return op;
        }
        if ((w & 0xFFFFFC1F) == 0xD65F0000) { op.Kind = A64Kind.Ret; op.Rn = (int)((w >> 5) & 0x1F); return op; }
        if ((w & 0xFFFFFC1F) == 0xD61F0000) { op.Kind = A64Kind.Br; op.Rn = (int)((w >> 5) & 0x1F); return op; }
        if ((w & 0xFFFFFC1F) == 0xD63F0000) { op.Kind = A64Kind.Blr; op.Rn = (int)((w >> 5) & 0x1F); return op; }
        if ((w & 0xFFE0001F) == 0xD4200000 || (w & 0xFFE0001F) == 0xD4400000 || (w & 0xFFFF0000) == 0x00000000)
        {
            op.Kind = A64Kind.Trap;
            return op;
        }
        if ((w & 0xFFFFF01F) == 0xD503201F) { op.Kind = A64Kind.Nop; return op; }

        var sf = (w & 0x80000000) != 0;
        op.Is64 = sf;
        op.Rd = (int)(w & 0x1F);
        op.Rn = (int)((w >> 5) & 0x1F);
        op.Rm = (int)((w >> 16) & 0x1F);

        if ((w & 0x1F000000) == 0x10000000)
        {
            var imm = SignExtend(((w >> 5) & 0x7FFFF) << 2 | ((w >> 29) & 3), 21);
            if ((w & 0x80000000) != 0)
            {
                op.Kind = A64Kind.Adrp;
                op.Target = unchecked((address & ~0xFFFUL) + (ulong)(imm << 12));
            }
            else
            {
                op.Kind = A64Kind.Adr;
                op.Target = unchecked(address + (ulong)imm);
            }
            op.Is64 = true;
            return op;
        }

        if ((w & 0x1F800000) == 0x11000000)
        {
            op.Kind = (w & 0x40000000) != 0 ? A64Kind.Sub : A64Kind.Add;
            op.SetsFlags = (w & 0x20000000) != 0;
            op.Imm = ((w >> 10) & 0xFFF) << ((w & 0x00400000) != 0 ? 12 : 0);
            op.HasImm = true;
            return op;
        }

        if ((w & 0x1F200000) == 0x0B000000)
        {
            op.Kind = (w & 0x40000000) != 0 ? A64Kind.Sub : A64Kind.Add;
            op.SetsFlags = (w & 0x20000000) != 0;
            op.Shift = (A64Shift)((w >> 22) & 3);
            op.ShiftAmount = (int)((w >> 10) & 0x3F);
            if (op.Shift == A64Shift.Ror)
                op.Kind = A64Kind.Unknown;
            return op;
        }

        if ((w & 0x1F000000) == 0x0A000000)
        {
            var opc = (w >> 29) & 3;
            op.Kind = opc switch { 0 => A64Kind.And, 1 => A64Kind.Orr, 2 => A64Kind.Eor, _ => A64Kind.And };
            op.SetsFlags = opc == 3;
            op.InvertRm = (w & 0x00200000) != 0;
            op.Shift = (A64Shift)((w >> 22) & 3);
            op.ShiftAmount = (int)((w >> 10) & 0x3F);
            if (op.Shift == A64Shift.Ror && op.ShiftAmount != 0)
                op.Kind = A64Kind.Unknown;
            return op;
        }

        if ((w & 0x1F800000) == 0x12000000)
        {
            var opc = (w >> 29) & 3;
            var n = (int)((w >> 22) & 1);
            var immr = (int)((w >> 16) & 0x3F);
            var imms = (int)((w >> 10) & 0x3F);
            if ((!sf && n == 1) || !DecodeBitMask(n, imms, immr, sf ? 64 : 32, out var mask))
            {
                op.Kind = A64Kind.Unknown;
                return op;
            }
            op.Kind = opc switch { 0 => A64Kind.And, 1 => A64Kind.Orr, 2 => A64Kind.Eor, _ => A64Kind.And };
            op.SetsFlags = opc == 3;
            op.Imm = unchecked((long)mask);
            op.HasImm = true;
            return op;
        }

        if ((w & 0x1F800000) == 0x12800000)
        {
            var opc = (w >> 29) & 3;
            var hw = (int)((w >> 21) & 3);
            if (!sf && hw > 1 || opc == 1)
            {
                op.Kind = A64Kind.Unknown;
                return op;
            }
            op.Kind = opc switch { 0 => A64Kind.Movn, 2 => A64Kind.Movz, _ => A64Kind.Movk };
            op.Imm = (w >> 5) & 0xFFFF;
            op.ShiftAmount = hw * 16;
            op.HasImm = true;
            return op;
        }

        if ((w & 0x1F800000) == 0x13000000)
        {
            var opc = (w >> 29) & 3;
            var n = (w >> 22) & 1;
            if (n != (sf ? 1u : 0u) || opc == 3)
            {
                op.Kind = A64Kind.Unknown;
                return op;
            }
            op.Kind = opc switch { 0 => A64Kind.Sbfm, 1 => A64Kind.Bfm, _ => A64Kind.Ubfm };
            op.Immr = (int)((w >> 16) & 0x3F);
            op.Imms = (int)((w >> 10) & 0x3F);
            return op;
        }

        if ((w & 0x1FE00000) == 0x1A800000 && (w & 0x20000000) == 0 && (w & 0x800) == 0)
        {
            var o = (w >> 30) & 1;
            var o2 = (w >> 10) & 1;
            op.Kind = (o, o2) switch { (0, 0) => A64Kind.Csel, (0, 1) => A64Kind.Csinc, (1, 0) => A64Kind.Csinv, _ => A64Kind.Csneg };
            op.Cond = (A64Cond)((w >> 12) & 0xF);
            return op;
        }

        if ((w & 0x7FE00000) == 0x1B000000)
        {
            op.Kind = (w & 0x8000) != 0 ? A64Kind.Msub : A64Kind.Madd;
            op.Ra = (int)((w >> 10) & 0x1F);
            return op;
        }

        if ((w & 0x7FE00000) == 0x1AC00000)
        {
            var opcode = (w >> 10) & 0x3F;
            op.Kind = opcode switch
            {
                0b000010 => A64Kind.Udiv,
                0b000011 => A64Kind.Sdiv,
                0b001000 => A64Kind.Lslv,
                0b001001 => A64Kind.Lsrv,
                0b001010 => A64Kind.Asrv,
                _ => A64Kind.Unknown
            };
            return op;
        }

        if ((w & 0x3A000000) == 0x28000000)
        {
            var mode = (w >> 23) & 3;
            if (mode == 0)
            {
                op.Kind = A64Kind.Unknown;
                return op;
            }
            var vector = (w & 0x04000000) != 0;
            var opc = (w >> 30) & 3;
            op.Kind = (w & 0x00400000) != 0 ? A64Kind.Ldp : A64Kind.Stp;
            op.Rt2 = (int)((w >> 10) & 0x1F);
            op.Rt = (int)(w & 0x1F);
            op.Vector = vector;
            op.Size = vector ? 4 << (int)opc : opc == 2 ? 8 : 4;
            op.SignedLoad = !vector && opc == 1;
            if (!vector && opc == 3 || vector && opc == 3)
                op.Kind = A64Kind.Unknown;
            op.Imm = SignExtend((w >> 15) & 0x7F, 7) * op.Size;
            op.Index = mode switch { 1 => A64Index.Post, 3 => A64Index.Pre, _ => A64Index.Offset };
            return op;
        }

        if ((w & 0x3A000000) == 0x38000000)
        {
            var size = (int)((w >> 30) & 3);
            var vector = (w & 0x04000000) != 0;
            var opc = (int)((w >> 22) & 3);
            op.Rt = (int)(w & 0x1F);
            op.Vector = vector;
            if ((w & 0x01000000) != 0)
            {
                op.Index = A64Index.Offset;
                op.Imm = (w >> 10) & 0xFFF;
            }
            else if ((w & 0x00200000) == 0)
            {
                var kind = (w >> 10) & 3;
                op.Index = kind switch { 0 => A64Index.Offset, 1 => A64Index.Post, 3 => A64Index.Pre, _ => A64Index.Unprivileged };
                op.Imm = SignExtend((w >> 12) & 0x1FF, 9);
                op.Unscaled = true;
            }
            else if (((w >> 10) & 3) == 2)
            {
                op.Index = A64Index.RegisterOffset;
                op.Rm = (int)((w >> 16) & 0x1F);
                op.Extend = (int)((w >> 13) & 7);
                op.ShiftAmount = (w & 0x1000) != 0 ? -1 : 0;
            }
            else
            {
                op.Kind = A64Kind.Unknown;
                return op;
            }

            if (op.Index == A64Index.Unprivileged)
            {
                op.Kind = A64Kind.Unknown;
                return op;
            }

            if (vector)
            {
                var bytes = opc >= 2 ? 16 : 1 << size;
                if (opc >= 2 && size != 0)
                {
                    op.Kind = A64Kind.Unknown;
                    return op;
                }
                op.Size = bytes;
                op.Kind = (opc & 1) != 0 ? A64Kind.Ldr : A64Kind.Str;
            }
            else
            {
                op.Size = 1 << size;
                switch (opc)
                {
                    case 0: op.Kind = A64Kind.Str; break;
                    case 1: op.Kind = A64Kind.Ldr; op.Is64 = size == 3; break;
                    case 2:
                        if (size == 3) { op.Kind = A64Kind.Unknown; return op; }
                        op.Kind = A64Kind.Ldr; op.SignedLoad = true; op.Is64 = true; break;
                    default:
                        if (size >= 2) { op.Kind = A64Kind.Unknown; return op; }
                        op.Kind = A64Kind.Ldr; op.SignedLoad = true; op.Is64 = false; break;
                }
                if (op.Kind == A64Kind.Str)
                    op.Is64 = size == 3;
            }
            if (op.Index == A64Index.Offset && !op.Unscaled)
                op.Imm *= op.Size;
            if (op.ShiftAmount == -1)
                op.ShiftAmount = op.Size switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 4 };
            return op;
        }

        if ((w & 0x5F200000) == 0x1E200000)
            return DecodeFloat(w, op);

        op.Kind = A64Kind.Unknown;
        return op;
    }

    private static A64Op DecodeFloat(uint w, A64Op op)
    {
        var type = (w >> 22) & 3;
        if (type > 1 || (w & 0x20000000) != 0)
        {
            op.Kind = A64Kind.Unknown;
            return op;
        }
        op.FloatDouble = type == 1;
        var bits10to15 = (w >> 10) & 0x3F;

        if ((w & 0x7F20FC00) == 0x1E200000)
        {
            var rmodeOpcode = (w >> 16) & 0x1F;
            op.Is64 = (w & 0x80000000) != 0;
            op.Kind = rmodeOpcode switch
            {
                0b00010 => A64Kind.Scvtf,
                0b00011 => A64Kind.Ucvtf,
                0b11000 => A64Kind.Fcvtzs,
                0b11001 => A64Kind.Fcvtzu,
                0b00110 => A64Kind.FmovToGeneral,
                0b00111 => A64Kind.FmovFromGeneral,
                _ => A64Kind.Unknown
            };
            if (op.Kind is A64Kind.FmovToGeneral or A64Kind.FmovFromGeneral && op.Is64 != op.FloatDouble)
                op.Kind = A64Kind.Unknown;
            return op;
        }

        if ((w & 0x80000000) != 0)
        {
            op.Kind = A64Kind.Unknown;
            return op;
        }

        if ((bits10to15 & 0x7) == 0b100 && ((w >> 5) & 0x1F) == 0)
        {
            op.Kind = A64Kind.FmovImm;
            op.FloatImm = ExpandFloatImm((w >> 13) & 0xFF, op.FloatDouble);
            return op;
        }
        if ((w & 0xFC1F) == 0x2000 || (w & 0xFC1F) == 0x2008 || (w & 0xFC1F) == 0x2010 || (w & 0xFC1F) == 0x2018)
        {
            op.Kind = A64Kind.Fcmp;
            op.CompareWithZero = (w & 0x8) != 0;
            return op;
        }
        if ((bits10to15 & 0x3) == 0b11)
        {
            op.Kind = A64Kind.Fcsel;
            op.Cond = (A64Cond)((w >> 12) & 0xF);
            return op;
        }
        if ((bits10to15 & 0x3) == 0b10)
        {
            op.Kind = ((w >> 12) & 0xF) switch
            {
                0 => A64Kind.Fmul,
                1 => A64Kind.Fdiv,
                2 => A64Kind.Fadd,
                3 => A64Kind.Fsub,
                _ => A64Kind.Unknown
            };
            return op;
        }
        if ((w & 0x7C00) == 0x4000 && (w & 0x00200000) != 0)
        {
            var opcode = (w >> 15) & 0x3F;
            op.Kind = opcode switch
            {
                0 => A64Kind.FmovReg,
                1 => A64Kind.Fabs,
                2 => A64Kind.Fneg,
                5 when !op.FloatDouble => A64Kind.FcvtToDouble,
                4 when op.FloatDouble => A64Kind.FcvtToSingle,
                _ => A64Kind.Unknown
            };
            return op;
        }
        op.Kind = A64Kind.Unknown;
        return op;
    }

    private static double ExpandFloatImm(uint imm8, bool isDouble)
    {
        var sign = (imm8 >> 7) & 1;
        var b6 = (imm8 >> 6) & 1;
        var exp3 = (imm8 >> 4) & 3;
        var frac = imm8 & 0xF;
        if (isDouble)
        {
            ulong bits = ((ulong)sign << 63) | ((ulong)(b6 ^ 1) << 62) | ((b6 == 1 ? 0xFFUL : 0UL) << 54) | ((ulong)exp3 << 52) | ((ulong)frac << 48);
            return BitConverter.Int64BitsToDouble((long)bits);
        }
        uint single = (sign << 31) | ((b6 ^ 1) << 30) | ((b6 == 1 ? 0x1Fu : 0u) << 25) | (exp3 << 23) | (frac << 19);
        return BitConverter.Int32BitsToSingle((int)single);
    }

    private static bool DecodeBitMask(int n, int imms, int immr, int width, out ulong mask)
    {
        mask = 0;
        var combined = (n << 6) | (~imms & 0x3F);
        var len = -1;
        for (var bit = 6; bit >= 0; bit--)
        {
            if ((combined & (1 << bit)) != 0)
            {
                len = bit;
                break;
            }
        }
        if (len < 1)
            return false;
        var levels = (1 << len) - 1;
        var s = imms & levels;
        var r = immr & levels;
        if (s == levels)
            return false;
        var esize = 1 << len;
        var welem = s + 1 == 64 ? ulong.MaxValue : (1UL << (s + 1)) - 1;
        var elemMask = esize == 64 ? ulong.MaxValue : (1UL << esize) - 1;
        var rotated = r == 0 ? welem : ((welem >> r) | (welem << (esize - r))) & elemMask;
        ulong result = 0;
        for (var i = 0; i < width; i += esize)
            result |= rotated << i;
        mask = width == 64 ? result : result & 0xFFFFFFFFUL;
        return true;
    }

    private static long SignExtend(uint value, int bits)
    {
        var shift = 64 - bits;
        return ((long)value << shift) >> shift;
    }
}

public enum A64Kind
{
    Unknown = 0,
    Nop, Trap,
    B, Bl, BCond, Cbz, Cbnz, Tbz, Tbnz, Ret, Br, Blr,
    Adr, Adrp,
    Add, Sub, And, Orr, Eor,
    Movz, Movn, Movk,
    Sbfm, Bfm, Ubfm,
    Csel, Csinc, Csinv, Csneg,
    Madd, Msub, Udiv, Sdiv, Lslv, Lsrv, Asrv,
    Ldr, Str, Ldp, Stp,
    FmovReg, FmovImm, FmovToGeneral, FmovFromGeneral, Fadd, Fsub, Fmul, Fdiv, Fneg, Fabs, Fcmp, Fcsel,
    Scvtf, Ucvtf, Fcvtzs, Fcvtzu, FcvtToDouble, FcvtToSingle
}

public enum A64Shift { Lsl = 0, Lsr = 1, Asr = 2, Ror = 3 }

public enum A64Index { Offset, Pre, Post, RegisterOffset, Unprivileged }

public enum A64Cond { Eq = 0, Ne, Cs, Cc, Mi, Pl, Vs, Vc, Hi, Ls, Ge, Lt, Gt, Le, Al, Nv }

public sealed class A64Op
{
    public uint Word { get; init; }
    public ulong Address { get; init; }
    public A64Kind Kind { get; set; }
    public bool Is64 { get; set; }
    public int Rd { get; set; }
    public int Rn { get; set; }
    public int Rm { get; set; }
    public int Ra { get; set; }
    public int Rt { get; set; }
    public int Rt2 { get; set; }
    public long Imm { get; set; }
    public bool HasImm { get; set; }
    public A64Shift Shift { get; set; }
    public int ShiftAmount { get; set; }
    public bool InvertRm { get; set; }
    public bool SetsFlags { get; set; }
    public int Immr { get; set; }
    public int Imms { get; set; }
    public A64Cond Cond { get; set; }
    public ulong Target { get; set; }
    public int Size { get; set; }
    public bool Vector { get; set; }
    public bool SignedLoad { get; set; }
    public bool Unscaled { get; set; }
    public A64Index Index { get; set; }
    public int Extend { get; set; }
    public bool FloatDouble { get; set; }
    public double FloatImm { get; set; }
    public bool CompareWithZero { get; set; }

    public bool IsBlockTerminator => Kind is A64Kind.B or A64Kind.BCond or A64Kind.Cbz or A64Kind.Cbnz or A64Kind.Tbz or A64Kind.Tbnz or A64Kind.Ret or A64Kind.Br or A64Kind.Trap;
    public bool IsConditionalBranch => Kind is A64Kind.BCond or A64Kind.Cbz or A64Kind.Cbnz or A64Kind.Tbz or A64Kind.Tbnz;
}

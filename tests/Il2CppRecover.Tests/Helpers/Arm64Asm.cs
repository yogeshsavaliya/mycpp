using System.Buffers.Binary;

namespace Il2CppRecover.Tests.Helpers;

public static class Arm64Asm
{
    public static byte[] Words(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    public static uint Ret() => 0xD65F03C0;
    public static uint Nop() => 0xD503201F;
    public static uint Movz(int rd, int imm) => 0x52800000u | ((uint)imm << 5) | (uint)rd;
    public static uint Movn(int rd, int imm) => 0x12800000u | ((uint)imm << 5) | (uint)rd;
    public static uint MovReg(int rd, int rm) => 0x2A0003E0u | ((uint)rm << 16) | (uint)rd;
    public static uint AddReg(int rd, int rn, int rm) => 0x0B000000u | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd;
    public static uint SubReg(int rd, int rn, int rm) => 0x4B000000u | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd;
    public static uint AddImm(int rd, int rn, int imm) => 0x11000000u | ((uint)imm << 10) | ((uint)rn << 5) | (uint)rd;
    public static uint Mul(int rd, int rn, int rm) => 0x1B000000u | ((uint)rm << 16) | (31u << 10) | ((uint)rn << 5) | (uint)rd;
    public static uint And(int rd, int rn, int rm) => 0x0A000000u | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd;
    public static uint Orr(int rd, int rn, int rm) => 0x2A000000u | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd;
    public static uint Eor(int rd, int rn, int rm) => 0x4A000000u | ((uint)rm << 16) | ((uint)rn << 5) | (uint)rd;
    public static uint CmpReg(int rn, int rm) => 0x6B00001Fu | ((uint)rm << 16) | ((uint)rn << 5);
    public static uint CmpImm(int rn, int imm) => 0x7100001Fu | ((uint)imm << 10) | ((uint)rn << 5);
    public static uint B(int byteDelta) => 0x14000000u | ((uint)(byteDelta >> 2) & 0x03FFFFFFu);
    public static uint Bl(int byteDelta) => 0x94000000u | ((uint)(byteDelta >> 2) & 0x03FFFFFFu);
    public static uint BCond(int cond, int byteDelta) => 0x54000000u | ((((uint)(byteDelta >> 2)) & 0x7FFFFu) << 5) | (uint)cond;
    public static uint Cbz(int rt, int byteDelta) => 0x34000000u | ((((uint)(byteDelta >> 2)) & 0x7FFFFu) << 5) | (uint)rt;
    public static uint Tbz(int rt, int bit, int byteDelta) => 0x36000000u | ((uint)(bit & 31) << 19) | ((((uint)(byteDelta >> 2)) & 0x3FFFu) << 5) | (uint)(rt & 31);
    public static uint Tbnz(int rt, int bit, int byteDelta) => Tbz(rt, bit, byteDelta) | 0x01000000u;
    public static uint LdrW(int rt, int rn, int byteOffset) => 0xB9400000u | ((uint)(byteOffset / 4) << 10) | ((uint)rn << 5) | (uint)rt;
    public static uint StrW(int rt, int rn, int byteOffset) => 0xB9000000u | ((uint)(byteOffset / 4) << 10) | ((uint)rn << 5) | (uint)rt;
    public static uint LdrB(int rt, int rn, int byteOffset) => 0x39400000u | ((uint)byteOffset << 10) | ((uint)rn << 5) | (uint)rt;
    public static uint StrB(int rt, int rn, int byteOffset) => 0x39000000u | ((uint)byteOffset << 10) | ((uint)rn << 5) | (uint)rt;
    public static uint LdrX(int rt, int rn, int byteOffset) => 0xF9400000u | ((uint)(byteOffset / 8) << 10) | ((uint)rn << 5) | (uint)rt;
    public static uint Cbz64(int rt, int byteDelta) => Cbz(rt, byteDelta) | 0x80000000u;
    public static uint MovReg64(int rd, int rm) => 0xAA0003E0u | ((uint)rm << 16) | (uint)rd;
    public static uint Adrp(int rd, int immlo, int immhi) => 0x90000000u | ((uint)immlo << 29) | ((uint)immhi << 5) | (uint)rd;

    public const int Eq = 0;
    public const int Ne = 1;
    public const int Cs = 2;
    public const int Cc = 3;
    public const int Hi = 8;
    public const int Ls = 9;
    public const int Ge = 10;
    public const int Lt = 11;
    public const int Gt = 12;
    public const int Le = 13;
}

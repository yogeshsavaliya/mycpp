using System.Buffers.Binary;
using System.Text;

namespace Il2CppRecover.Binary;

/// <summary>
/// Reads ELF64 little-endian function symbols and <c>.rela.plt</c> stubs.
/// A stub address is <c>.plt + header + index * 16</c>. The header is the size of
/// <c>.plt</c> left after one 16-byte entry per relocation, and it is accepted only
/// when that remainder is 16 or 32 bytes.
/// </summary>
public static class ElfCodeSymbols
{
    public static Dictionary<ulong, string> Read(ReadOnlySpan<byte> elf)
    {
        var map = new Dictionary<ulong, string>();
        if (elf.Length < 64 || elf[0] != 0x7F || elf[1] != (byte)'E' || elf[2] != (byte)'L' || elf[3] != (byte)'F' || elf[4] != 2 || elf[5] != 1)
            return map;
        var shoff = BinaryPrimitives.ReadUInt64LittleEndian(elf[40..]);
        var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(elf[58..]);
        var shnum = BinaryPrimitives.ReadUInt16LittleEndian(elf[60..]);
        var shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(elf[62..]);
        if (shentsize < 64 || shnum == 0 || shnum > 1024 || shstrndx >= shnum)
            return map;
        if (!TrySection(elf, shoff, shentsize, shstrndx, out var strOff, out var strSize, out _, out _, out _, out _))
            return map;
        int dynsym = -1;
        int plt = -1;
        int rela = -1;
        for (var i = 0; i < shnum; i++)
        {
            if (!TrySection(elf, shoff, shentsize, i, out var off, out var size, out var addr, out var type, out var nameOff, out _))
                continue;
            var name = CString(elf, strOff, strSize, nameOff);
            if (type == 11)
                dynsym = i;
            else if (name == ".plt")
                plt = i;
            else if (name == ".rela.plt")
                rela = i;
            _ = off;
            _ = size;
            _ = addr;
        }
        if (dynsym < 0 || !TrySection(elf, shoff, shentsize, dynsym, out var symOff, out var symSize, out _, out _, out _, out var link))
            return map;
        if (!TrySection(elf, shoff, shentsize, (int)link, out var dynstrOff, out var dynstrSize, out _, out _, out _, out _))
            return map;
        var symEnt = SectionEntsize(elf, shoff, shentsize, dynsym);
        if (symEnt < 24)
            symEnt = 24;
        var count = (int)(symSize / symEnt);
        for (var i = 0; i < count; i++)
        {
            var at = symOff + (ulong)i * symEnt;
            if (at + 24 > (ulong)elf.Length)
                break;
            var nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(elf[(int)at..]);
            var info = elf[(int)at + 4];
            var shndx = BinaryPrimitives.ReadUInt16LittleEndian(elf[((int)at + 6)..]);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(elf[((int)at + 8)..]);
            if (shndx == 0 || value == 0 || (info & 0xF) is not (2 or 0))
                continue;
            var text = CString(elf, dynstrOff, dynstrSize, nameIndex);
            if (text.Length > 0)
                map.TryAdd(value, text);
        }
        if (plt >= 0 && rela >= 0
            && TrySection(elf, shoff, shentsize, plt, out _, out var pltSize, out var pltAddr, out _, out _, out _)
            && TrySection(elf, shoff, shentsize, rela, out var relaOff, out var relaSize, out _, out _, out _, out _))
        {
            var relas = (int)(relaSize / 24);
            var header = pltSize - (ulong)relas * 16;
            if (header is 16 or 32)
            {
                for (var i = 0; i < relas; i++)
                {
                    var at = relaOff + (ulong)i * 24;
                    if (at + 16 > (ulong)elf.Length)
                        break;
                    var info = BinaryPrimitives.ReadUInt64LittleEndian(elf[((int)at + 8)..]);
                    var index = (int)(info >> 32);
                    var symAt = symOff + (ulong)index * symEnt;
                    if (index < 0 || symAt + 4 > (ulong)elf.Length)
                        continue;
                    var nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(elf[(int)symAt..]);
                    var text = CString(elf, dynstrOff, dynstrSize, nameIndex);
                    if (text.Length > 0)
                        map.TryAdd(pltAddr + header + (ulong)i * 16, text);
                }
            }
        }
        return map;
    }

    private static ulong SectionEntsize(ReadOnlySpan<byte> elf, ulong shoff, int shentsize, int index)
    {
        var entry = shoff + (ulong)index * (ulong)shentsize;
        return entry + 64 <= (ulong)elf.Length ? BinaryPrimitives.ReadUInt64LittleEndian(elf[((int)entry + 56)..]) : 0;
    }

    private static bool TrySection(ReadOnlySpan<byte> elf, ulong shoff, int shentsize, int index, out ulong offset, out ulong size, out ulong address, out uint type, out uint name, out uint link)
    {
        offset = size = address = 0;
        type = name = link = 0;
        var entry = shoff + (ulong)index * (ulong)shentsize;
        if (entry + 64 > (ulong)elf.Length)
            return false;
        name = BinaryPrimitives.ReadUInt32LittleEndian(elf[(int)entry..]);
        type = BinaryPrimitives.ReadUInt32LittleEndian(elf[((int)entry + 4)..]);
        address = BinaryPrimitives.ReadUInt64LittleEndian(elf[((int)entry + 16)..]);
        offset = BinaryPrimitives.ReadUInt64LittleEndian(elf[((int)entry + 24)..]);
        size = BinaryPrimitives.ReadUInt64LittleEndian(elf[((int)entry + 32)..]);
        link = BinaryPrimitives.ReadUInt32LittleEndian(elf[((int)entry + 40)..]);
        return offset <= (ulong)elf.Length && size <= (ulong)elf.Length - offset;
    }

    private static string CString(ReadOnlySpan<byte> elf, ulong table, ulong tableSize, uint index)
    {
        if ((ulong)index >= tableSize)
            return "";
        var start = table + index;
        var end = table + tableSize;
        var chars = new List<byte>();
        for (var i = start; i < end && i < (ulong)elf.Length; i++)
        {
            var b = elf[(int)i];
            if (b == 0)
                break;
            chars.Add(b);
            if (chars.Count > 512)
                break;
        }
        return Encoding.UTF8.GetString(chars.ToArray());
    }
}

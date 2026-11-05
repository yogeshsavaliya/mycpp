using System.Buffers.Binary;
using System.Text;

namespace Il2CppRecover.Binary;

public sealed class ElfImage
{
    public bool Is64Bit { get; init; }
    public bool LittleEndian { get; init; }
    public ushort Type { get; init; }
    public ushort Machine { get; init; }
    public NativeArchitecture Architecture { get; init; }
    public IReadOnlyList<ElfSegment> Segments { get; init; } = [];
    public string Note { get; init; } = "";

    public bool TryMap(ulong virtualAddress, out long fileOffset)
    {
        foreach (var segment in Segments)
        {
            if (segment.Type != 1)
                continue;
            if (virtualAddress < segment.VirtualAddress)
                continue;
            var delta = virtualAddress - segment.VirtualAddress;
            if (delta >= segment.FileSize)
                continue;
            fileOffset = checked((long)(segment.FileOffset + delta));
            return true;
        }
        fileOffset = 0;
        return false;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out ElfImage? image, out string error)
    {
        image = null;
        if (data.Length < 64 || data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
        {
            error = "The native file is not an ELF image.";
            return false;
        }

        var klass = data[4];
        var endian = data[5];
        if (klass is not (1 or 2))
        {
            error = "The ELF class is neither 32-bit nor 64-bit.";
            return false;
        }
        if (endian is not (1 or 2))
        {
            error = "The ELF endianness byte is not little-endian or big-endian.";
            return false;
        }

        var is64 = klass == 2;
        var little = endian == 1;
        if (!little)
        {
            error = "Big-endian ELF images are recognised and rejected. This milestone reads little-endian images only.";
            image = new ElfImage { Is64Bit = is64, LittleEndian = false, Architecture = NativeArchitecture.Unknown, Note = error };
            return false;
        }

        var headerSize = is64 ? 64 : 52;
        if (data.Length < headerSize)
        {
            error = "The ELF header is truncated.";
            return false;
        }

        var type = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
        var phoff = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(data[32..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(data[(is64 ? 54 : 42)..]);
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(data[(is64 ? 56 : 44)..]);
        if (phnum > 128)
        {
            error = "The ELF program header count is above the inspection limit of 128.";
            return false;
        }
        var expectedEnt = is64 ? 56 : 32;
        if (phentsize < expectedEnt)
        {
            error = "The ELF program header entry size is smaller than the architecture requires.";
            return false;
        }

        var segments = new List<ElfSegment>();
        for (var i = 0; i < phnum; i++)
        {
            var entry = phoff + (ulong)i * phentsize;
            if (entry + (ulong)expectedEnt > (ulong)data.Length)
            {
                error = "A program header entry extends past the file.";
                return false;
            }
            var slice = data[(int)entry..];
            uint pType;
            uint flags;
            ulong offset;
            ulong vaddr;
            ulong filesz;
            ulong memsz;
            if (is64)
            {
                pType = BinaryPrimitives.ReadUInt32LittleEndian(slice);
                flags = BinaryPrimitives.ReadUInt32LittleEndian(slice[4..]);
                offset = BinaryPrimitives.ReadUInt64LittleEndian(slice[8..]);
                vaddr = BinaryPrimitives.ReadUInt64LittleEndian(slice[16..]);
                filesz = BinaryPrimitives.ReadUInt64LittleEndian(slice[32..]);
                memsz = BinaryPrimitives.ReadUInt64LittleEndian(slice[40..]);
            }
            else
            {
                pType = BinaryPrimitives.ReadUInt32LittleEndian(slice);
                offset = BinaryPrimitives.ReadUInt32LittleEndian(slice[4..]);
                vaddr = BinaryPrimitives.ReadUInt32LittleEndian(slice[8..]);
                filesz = BinaryPrimitives.ReadUInt32LittleEndian(slice[16..]);
                memsz = BinaryPrimitives.ReadUInt32LittleEndian(slice[20..]);
                flags = BinaryPrimitives.ReadUInt32LittleEndian(slice[24..]);
            }
            segments.Add(new ElfSegment
            {
                Type = pType,
                Flags = flags,
                FileOffset = offset,
                VirtualAddress = vaddr,
                FileSize = filesz,
                MemorySize = memsz
            });
        }

        image = new ElfImage
        {
            Is64Bit = is64,
            LittleEndian = true,
            Type = type,
            Machine = machine,
            Architecture = machine switch
            {
                183 => NativeArchitecture.Arm64,
                40 => NativeArchitecture.Arm32,
                62 => NativeArchitecture.X64,
                3 => NativeArchitecture.X86,
                _ => NativeArchitecture.Other
            },
            Segments = segments,
            Note = Describe(type, machine)
        };
        error = "";
        return true;
    }

    public static string MachineName(NativeArchitecture architecture) => architecture switch
    {
        NativeArchitecture.Arm64 => "AArch64",
        NativeArchitecture.Arm32 => "ARM",
        NativeArchitecture.X64 => "x86-64",
        NativeArchitecture.X86 => "x86",
        _ => "other"
    };

    private static string Describe(ushort type, ushort machine)
    {
        var kind = type switch
        {
            1 => "relocatable",
            2 => "executable",
            3 => "shared object",
            4 => "core",
            _ => "type " + type
        };
        return "ELF " + kind + ", e_machine=" + machine;
    }
}

public sealed class ElfSegment
{
    public uint Type { get; init; }
    public uint Flags { get; init; }
    public ulong FileOffset { get; init; }
    public ulong VirtualAddress { get; init; }
    public ulong FileSize { get; init; }
    public ulong MemorySize { get; init; }
}

public static class ElfBuilder
{
    public static byte[] SharedObject(ushort machine, byte[]? payload = null)
    {
        payload ??= [0, 0, 0, 0];
        var file = new byte[64 + 56 + payload.Length];
        file[0] = 0x7F;
        file[1] = (byte)'E';
        file[2] = (byte)'L';
        file[3] = (byte)'F';
        file[4] = 2;
        file[5] = 1;
        file[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(18), machine);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(58), 64);

        var ph = 64;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(ph), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(ph + 4), 5);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(ph + 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(ph + 16), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(ph + 32), (ulong)file.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(ph + 40), (ulong)file.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(ph + 48), 0x1000);
        payload.CopyTo(file.AsSpan(64 + 56));
        return file;
    }

    public static string DescribeBytes(byte[] data)
    {
        var text = new StringBuilder();
        text.Append(data.Length).Append(" bytes");
        return text.ToString();
    }
}

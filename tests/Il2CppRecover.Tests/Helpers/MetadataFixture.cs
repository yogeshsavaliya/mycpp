using System.Text;

namespace Il2CppRecover.Tests.Helpers;

/// <summary>
/// Builds a minimal metadata v31 file whose table layout matches the pinned LibCpp2IL reader.
/// It is a synthetic fixture, not a Unity-produced global-metadata.dat.
/// </summary>
public static class MetadataFixture
{
    public static byte[] Version31(string imageName, string assemblyName, string namespaceName, string typeName, string methodName, string[] parameterNames, bool isStatic)
    {
        var strings = new StringHeap();
        strings.Intern("");
        var image = strings.Intern(imageName);
        var assembly = strings.Intern(assemblyName);
        var ns = strings.Intern(namespaceName);
        var type = strings.Intern(typeName);
        var method = strings.Intern(methodName);
        var parameterIndices = parameterNames.Select(strings.Intern).ToArray();

        var parameters = new MemoryStream();
        for (var i = 0; i < parameterIndices.Length; i++)
        {
            WriteInt(parameters, parameterIndices[i]);
            WriteUInt(parameters, (uint)(0x08000001 + i));
            WriteInt(parameters, -1);
        }

        var methods = new MemoryStream();
        WriteInt(methods, method);
        WriteInt(methods, 0);
        WriteInt(methods, -1);
        WriteUInt(methods, 0);
        WriteInt(methods, parameterIndices.Length == 0 ? -1 : 0);
        WriteInt(methods, -1);
        WriteUInt(methods, 0x06000001);
        WriteUShort(methods, (ushort)(0x0006 | 0x0080 | (isStatic ? 0x0010 : 0)));
        WriteUShort(methods, 0);
        WriteUShort(methods, 0);
        WriteUShort(methods, (ushort)parameterIndices.Length);

        var types = new MemoryStream();
        WriteInt(types, type);
        WriteInt(types, ns);
        WriteInt(types, -1);
        WriteInt(types, -1);
        WriteInt(types, -1);
        WriteInt(types, -1);
        WriteInt(types, -1);
        WriteUInt(types, 0x00100001);
        WriteInt(types, -1);
        WriteInt(types, 0);
        WriteInt(types, -1);
        WriteInt(types, -1);
        WriteInt(types, -1);
        WriteInt(types, 0);
        WriteInt(types, 0);
        WriteInt(types, -1);
        WriteUShort(types, 1);
        WriteUShort(types, 0);
        WriteUShort(types, 0);
        WriteUShort(types, 0);
        WriteUShort(types, 0);
        WriteUShort(types, 0);
        WriteUShort(types, 0);
        WriteUShort(types, 0);
        WriteUInt(types, 0);
        WriteUInt(types, 0x02000002);

        var images = new MemoryStream();
        WriteInt(images, image);
        WriteInt(images, 0);
        WriteInt(images, 0);
        WriteUInt(images, 1);
        WriteInt(images, -1);
        WriteUInt(images, 0);
        WriteInt(images, -1);
        WriteUInt(images, 1);
        WriteInt(images, -1);
        WriteUInt(images, 0);

        var assemblies = new MemoryStream();
        WriteInt(assemblies, 0);
        WriteUInt(assemblies, 0x20000001);
        WriteInt(assemblies, -1);
        WriteInt(assemblies, 0);
        WriteInt(assemblies, assembly);
        WriteInt(assemblies, 0);
        WriteInt(assemblies, 0);
        WriteUInt(assemblies, 0);
        WriteInt(assemblies, 0);
        WriteUInt(assemblies, 0);
        WriteInt(assemblies, 0);
        WriteInt(assemblies, 0);
        WriteInt(assemblies, 0);
        WriteInt(assemblies, 0);
        WriteULong(assemblies, 0);

        var stringBytes = strings.ToArray();
        var sections = new byte[31][];
        for (var i = 0; i < sections.Length; i++)
            sections[i] = [];
        sections[2] = stringBytes;
        sections[5] = methods.ToArray();
        sections[10] = parameters.ToArray();
        sections[19] = types.ToArray();
        sections[20] = images.ToArray();
        sections[21] = assemblies.ToArray();

        const int headerSize = 256;
        var padding = new byte[128];
        var body = new MemoryStream();
        body.Write(padding);
        var offsets = new int[31];
        var sizes = new int[31];
        var cursor = headerSize + padding.Length;
        for (var i = 0; i < 31; i++)
        {
            if (sections[i].Length == 0)
            {
                offsets[i] = headerSize;
                sizes[i] = 0;
                continue;
            }
            offsets[i] = cursor;
            sizes[i] = sections[i].Length;
            body.Write(sections[i]);
            cursor += sections[i].Length;
        }

        var output = new MemoryStream();
        WriteUInt(output, 0xFAB11BAF);
        WriteInt(output, 31);
        for (var i = 0; i < 31; i++)
        {
            WriteInt(output, offsets[i]);
            WriteInt(output, sizes[i]);
        }
        if (output.Length != headerSize)
            throw new InvalidOperationException("Metadata header size is " + output.Length + ", expected " + headerSize + ".");
        output.Write(body.ToArray());
        return output.ToArray();
    }

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt(Stream stream, uint value) => WriteInt(stream, unchecked((int)value));

    private static void WriteUShort(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteULong(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private sealed class StringHeap
    {
        private readonly MemoryStream _stream = new();
        public int Intern(string text)
        {
            var offset = (int)_stream.Length;
            var bytes = Encoding.UTF8.GetBytes(text);
            _stream.Write(bytes);
            _stream.WriteByte(0);
            return offset;
        }
        public byte[] ToArray() => _stream.ToArray();
    }
}

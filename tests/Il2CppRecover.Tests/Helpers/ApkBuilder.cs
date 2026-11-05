using System.IO.Compression;
using System.Text;

namespace Il2CppRecover.Tests.Helpers;

public static class ApkBuilder
{
    public static void Write(string path, IEnumerable<(string Name, byte[] Bytes, CompressionLevel Level)> entries)
    {
        if (File.Exists(path))
            File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes, level) in entries)
        {
            var entry = zip.CreateEntry(name, level);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
    }

    public static byte[] GlobalGameManagers(string version)
    {
        var bytes = new byte[0x80];
        var encoded = Encoding.ASCII.GetBytes(version);
        encoded.CopyTo(bytes, 0x14);
        return bytes;
    }
}

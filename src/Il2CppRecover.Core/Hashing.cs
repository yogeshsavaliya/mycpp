using System.Security.Cryptography;

namespace Il2CppRecover;

public static class Hashing
{
    public static string Sha256(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

using System.Text.Json;

namespace Il2CppRecover.Tests.Helpers;

/// <summary>Loads the clang-built IL2CPP-shaped ARM64 fixture. It is not Unity output.</summary>
public sealed class FixtureBinary
{
    public required byte[] Bytes { get; init; }
    public required ElfImage Elf { get; init; }
    public required IReadOnlyDictionary<string, ulong> Symbols { get; init; }

    public static FixtureBinary Load()
    {
        var dir = Path.Combine(RepoRoot(), "fixtures", "Il2CppStyleArm64");
        var bytes = File.ReadAllBytes(Path.Combine(dir, "libfixture.so"));
        if (!ElfImage.TryParse(bytes, out var elf, out var error) || elf is null)
            throw new InvalidOperationException("Fixture ELF did not parse: " + error);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "symbols.json")));
        var symbols = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("address").GetUInt64());
        return new FixtureBinary { Bytes = bytes, Elf = elf, Symbols = symbols };
    }

    public static string RepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("IL2CPP_RECOVER_REPO");
        if (!string.IsNullOrEmpty(env))
            return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Il2CppRecover.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    public ulong Address(string symbol) => Symbols[symbol];

    public byte[] NextEntrySlice(ulong address)
    {
        var ordered = Symbols.Values.Order().ToList();
        var index = ordered.IndexOf(address);
        var end = index + 1 < ordered.Count ? ordered[index + 1] : address + 64;
        if (!Elf.TryMap(address, out var offset))
            throw new InvalidOperationException("Address does not map.");
        return Bytes.AsSpan((int)offset, (int)(end - address)).ToArray();
    }
}

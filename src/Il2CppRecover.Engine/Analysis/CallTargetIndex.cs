using Il2CppRecover.Recovery;

namespace Il2CppRecover.Analysis;

public static class CallTargetIndex
{
    public sealed record Result(
        IReadOnlyDictionary<ulong, MethodContract> Resolved,
        IReadOnlyDictionary<ulong, IReadOnlyList<MethodContract>> All,
        int SharedAddresses);

    public static IReadOnlyList<ulong> ExecutableEntries(IEnumerable<ulong> addresses)
    {
        var list = new List<ulong>();
        var seen = new HashSet<ulong>();
        foreach (var address in addresses)
        {
            if (address == 0 || !seen.Add(address))
                continue;
            list.Add(address);
        }
        return list;
    }

    public static Dictionary<int, T> IndexByMetadata<T>(IEnumerable<T> items, Func<T, int> index, Func<T, string> describe)
    {
        var map = new Dictionary<int, T>();
        foreach (var item in items)
        {
            var key = index(item);
            if (map.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    "Two records share metadata index " + key +
                    ". First: " + describe(existing) +
                    ". Second: " + describe(item) + ".");
            }
            map.Add(key, item);
        }
        return map;
    }

    public static Result Build(IEnumerable<(ulong Address, MethodContract Contract)> methods)
    {
        var groups = new Dictionary<ulong, List<MethodContract>>();
        foreach (var (address, contract) in methods)
        {
            if (address == 0)
                continue;
            if (!groups.TryGetValue(address, out var list))
            {
                list = [];
                groups.Add(address, list);
            }
            list.Add(contract);
        }

        var resolved = new Dictionary<ulong, MethodContract>(groups.Count);
        var all = new Dictionary<ulong, IReadOnlyList<MethodContract>>(groups.Count);
        var shared = 0;
        foreach (var (address, list) in groups)
        {
            all[address] = list;
            if (list.Count == 1)
            {
                resolved[address] = list[0];
                continue;
            }
            shared++;
            var names = string.Join(", ", list.Select(Describe));
            resolved[address] = new MethodContract
            {
                Name = list[0].Name,
                Namespace = list[0].Namespace,
                DeclaringType = list[0].DeclaringType,
                Assembly = list[0].Assembly,
                IsStatic = list[0].IsStatic,
                IsVirtual = list[0].IsVirtual,
                IsAbstract = list[0].IsAbstract,
                ReturnType = list[0].ReturnType,
                DeclaredReturnType = list[0].DeclaredReturnType,
                Parameters = list[0].Parameters,
                UnsupportedReason = "several metadata methods share native address 0x" + address.ToString("X") + ": " + names
            };
        }
        return new Result(resolved, all, shared);
    }

    private static string Describe(MethodContract contract) =>
        (string.IsNullOrEmpty(contract.Namespace) ? "" : contract.Namespace + ".") + contract.DeclaringType + "::" + contract.Name;
}

using System.Text;

namespace Il2CppRecover.Recovery;

/// <summary>
/// Converts a LibCpp2IL type name to C# syntax. Nesting uses '+'. Generic arity markers
/// are split across nesting levels: Outer`1+Inner`1&lt;A, B&gt; becomes Outer&lt;A&gt;.Inner&lt;B&gt;.
/// A name that does not parse is returned as "unresolved".
/// </summary>
public static class TypeNameFormatter
{
    public static string ToCSharp(string? metadataName)
    {
        if (string.IsNullOrWhiteSpace(metadataName))
            return "unresolved";
        var text = metadataName.Trim();
        if (text == "unresolved")
            return text;
        var pos = 0;
        try
        {
            var result = ParseType(text, ref pos);
            return pos == text.Length ? result : "unresolved";
        }
        catch (FormatException)
        {
            return "unresolved";
        }
    }

    private static string ParseType(string s, ref int pos)
    {
        var start = pos;
        while (pos < s.Length)
        {
            if (s[pos] == '<' && (pos == start || s[pos - 1] is '.' or '+'))
            {
                var close = s.IndexOf('>', pos);
                if (close < 0)
                    throw new FormatException();
                pos = close + 1;
                continue;
            }
            if (s[pos] is '<' or '>' or ',' or '[' or '*' or '&')
                break;
            pos++;
        }
        var named = s[start..pos].Trim();
        if (named.Length == 0)
            throw new FormatException();
        var args = new List<string>();
        if (pos < s.Length && s[pos] == '<')
        {
            pos++;
            while (true)
            {
                SkipSpaces(s, ref pos);
                args.Add(ParseType(s, ref pos));
                SkipSpaces(s, ref pos);
                if (pos >= s.Length)
                    throw new FormatException();
                if (s[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (s[pos] == '>')
                {
                    pos++;
                    break;
                }
                throw new FormatException();
            }
        }

        var core = RenderNamed(named, args);
        var suffix = new StringBuilder();
        var byRef = false;
        while (pos < s.Length)
        {
            if (s[pos] == '[' && pos + 1 < s.Length && s[pos + 1] == ']')
            {
                suffix.Append("[]");
                pos += 2;
            }
            else if (s[pos] == '*')
            {
                suffix.Append('*');
                pos++;
            }
            else if (s[pos] == '&')
            {
                byRef = true;
                pos++;
            }
            else
                break;
        }
        return (byRef ? "ref " : "") + core + suffix;
    }

    private static void SkipSpaces(string s, ref int pos)
    {
        while (pos < s.Length && s[pos] == ' ')
            pos++;
    }

    private static string RenderNamed(string named, List<string> args)
    {
        var segments = named.Split('+');
        var arities = new int[segments.Length];
        var names = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var tick = seg.LastIndexOf('`');
            if (tick >= 0 && int.TryParse(seg[(tick + 1)..], out var arity))
            {
                arities[i] = arity;
                names[i] = seg[..tick];
            }
            else
            {
                names[i] = seg;
            }
            names[i] = string.Join('.', names[i].Split('.').Select(SanitizePart));
        }

        var declared = arities.Sum();
        if (args.Count > 0 && declared == 0)
        {
            arities[^1] = args.Count;
            declared = args.Count;
        }
        if (args.Count > 0 && declared != args.Count)
            throw new FormatException();
        if (args.Count == 0 && declared > 0)
        {
            var open = new StringBuilder();
            for (var i = 0; i < names.Length; i++)
            {
                if (i > 0)
                    open.Append('.');
                open.Append(names[i]);
                if (arities[i] > 0)
                    open.Append('<').Append(new string(',', arities[i] - 1)).Append('>');
            }
            return open.ToString();
        }

        var sb = new StringBuilder();
        var used = 0;
        for (var i = 0; i < names.Length; i++)
        {
            if (i > 0)
                sb.Append('.');
            sb.Append(names[i]);
            if (arities[i] > 0)
            {
                sb.Append('<').Append(string.Join(", ", args.Skip(used).Take(arities[i]))).Append('>');
                used += arities[i];
            }
        }
        var full = sb.ToString();
        return Aliases.TryGetValue(full, out var alias) ? alias : full;
    }

    private static string SanitizePart(string part)
    {
        if (part.Length == 0)
            return "_";
        var chars = part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var text = new string(chars);
        if (!char.IsLetter(text[0]) && text[0] != '_')
            text = "_" + text;
        return text;
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["System.Void"] = "void",
        ["System.Boolean"] = "bool",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "uint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong",
        ["System.Single"] = "float",
        ["System.Double"] = "double",
        ["System.Byte"] = "byte",
        ["System.SByte"] = "sbyte",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
        ["System.Char"] = "char",
        ["System.String"] = "string",
        ["System.Object"] = "object",
        ["System.Decimal"] = "decimal"
    };
}

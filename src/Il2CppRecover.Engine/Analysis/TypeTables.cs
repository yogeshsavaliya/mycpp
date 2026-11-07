using Il2CppRecover.EngineAdapter;
using Il2CppRecover.Recovery;

namespace Il2CppRecover.Analysis;

/// <summary>
/// Type identity is the metadata type-definition index. A display name may belong to
/// several definitions. Field layouts include inherited instance fields.
/// </summary>
public sealed class TypeTables
{
    public required Dictionary<int, EngineTypeInfo> ById { get; init; }
    public required Dictionary<string, List<EngineTypeInfo>> ByName { get; init; }
    public required Dictionary<int, IReadOnlyList<FieldContract>> FieldsById { get; init; }
    public required Dictionary<int, string> LayoutNotes { get; init; }
    public int SameNameGroups => ByName.Count(pair => pair.Value.Count > 1);

    public static TypeTables Build(IEnumerable<EngineTypeInfo> types)
    {
        var byId = new Dictionary<int, EngineTypeInfo>();
        var byName = new Dictionary<string, List<EngineTypeInfo>>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (type.MetadataIndex < 0)
                continue;
            if (byId.TryGetValue(type.MetadataIndex, out var existing))
            {
                throw new InvalidOperationException(
                    "Two type definitions share metadata index " + type.MetadataIndex +
                    ". First: " + Describe(existing) +
                    ". Second: " + Describe(type) + ".");
            }
            byId.Add(type.MetadataIndex, type);
            var name = string.IsNullOrEmpty(type.DisplayName) ? FallbackName(type) : type.DisplayName;
            if (!byName.TryGetValue(name, out var list))
            {
                list = [];
                byName.Add(name, list);
            }
            list.Add(type);
        }

        var fields = new Dictionary<int, IReadOnlyList<FieldContract>>();
        var notes = new Dictionary<int, string>();
        var skipped = new Dictionary<int, List<string>>();
        var stack = new HashSet<int>();
        foreach (var id in byId.Keys)
            Layout(id, byId, fields, notes, skipped, stack);
        return new TypeTables { ById = byId, ByName = byName, FieldsById = fields, LayoutNotes = notes };
    }

    public static string Describe(EngineTypeInfo type) =>
        type.Image + " " + (string.IsNullOrEmpty(type.DisplayName) ? FallbackName(type) : type.DisplayName) + " index " + type.MetadataIndex;

    private static string FallbackName(EngineTypeInfo type) =>
        (string.IsNullOrEmpty(type.Namespace) ? "" : type.Namespace + ".") + type.Name;

    private static IReadOnlyList<FieldContract> Layout(int id, Dictionary<int, EngineTypeInfo> byId, Dictionary<int, IReadOnlyList<FieldContract>> memo, Dictionary<int, string> notes, Dictionary<int, List<string>> skipped, HashSet<int> stack)
    {
        if (memo.TryGetValue(id, out var done))
            return done;
        if (!stack.Add(id))
        {
            throw new InvalidOperationException(
                "Type definition index " + id + " (" + Describe(byId[id]) + ") is part of a base-type cycle.");
        }
        var type = byId[id];
        var fields = new List<FieldContract>();
        var seen = new Dictionary<int, FieldContract>();
        var overlapped = new Dictionary<int, List<string>>();
        var baseRef = type.BaseTypeRef;
        if (baseRef is { Kind: TypeRefKind.GenericInstance })
        {
            Note(notes, id, "Inherited fields of generic base " + baseRef.Display + " are unresolved: LibCpp2IL has no instantiated layout for that base.");
        }
        else if (baseRef is { Kind: TypeRefKind.Definition, DefinitionIndex: int baseId })
        {
            if (!byId.ContainsKey(baseId))
                Note(notes, id, "Base type index " + baseId + " (" + baseRef.Display + ") is not in the metadata type table, so its fields were not added.");
            else
            {
                foreach (var field in Layout(baseId, byId, memo, notes, skipped, stack))
                    AddField(type, field, fields, seen, overlapped);
                if (skipped.TryGetValue(baseId, out var inherited))
                    Remember(skipped, id, inherited);
            }
        }
        var display = string.IsNullOrEmpty(type.DisplayName) ? FallbackName(type) : type.DisplayName;
        if (type.IsOpenGeneric)
        {
            Note(notes, id, "Instance field offsets of open generic " + display + " are unresolved: LibCpp2IL has no instantiated field-offset table for that definition.");
        }
        else
        {
            foreach (var field in type.Fields)
            {
                if (field.IsStatic || field.IsLiteral || field.Offset is null)
                    continue;
                if (!Liftable(field, byId, out var kind))
                {
                    Remember(skipped, id, ["0x" + field.Offset.Value.ToString("X") + " " + display + "::" + field.Name + " (" + field.Type + ")"]);
                    continue;
                }
                var contract = new FieldContract
                {
                    Name = field.Name,
                    Type = kind,
                    DeclaredType = field.Type,
                    DeclaredTypeId = DefinitionId(field.TypeReference),
                    Access = field.Access,
                    InstanceOffset = field.Offset,
                    OwnerType = display,
                    OwnerTypeId = type.MetadataIndex
                };
                AddField(type, contract, fields, seen, overlapped);
            }
            AddStaticFields(type, display, fields, notes, id, byId);
        }
        if (skipped.TryGetValue(id, out var skips) && skips.Count > 0)
            Note(notes, id, "Fields at these offsets are outside the lifter subset: " + string.Join("; ", skips) + ".");
        if (overlapped.Count > 0)
        {
            fields.RemoveAll(field => field.InstanceOffset is int offset && overlapped.ContainsKey(offset));
            foreach (var pair in overlapped.OrderBy(pair => pair.Key))
                Note(notes, id, "Explicit layout shares offset 0x" + pair.Key.ToString("X") + " among " + string.Join(", ", pair.Value) + ". No one field was chosen.");
        }
        stack.Remove(id);
        memo[id] = fields;
        return fields;
    }

    private static void Remember(Dictionary<int, List<string>> skipped, int id, IEnumerable<string> items)
    {
        if (!skipped.TryGetValue(id, out var list))
            skipped[id] = list = [];
        foreach (var item in items)
        {
            if (!list.Contains(item))
                list.Add(item);
        }
    }

    private static void Note(Dictionary<int, string> notes, int id, string text)
    {
        notes[id] = notes.TryGetValue(id, out var existing) ? existing + " " + text : text;
    }

    private static void AddField(EngineTypeInfo type, FieldContract field, List<FieldContract> fields, Dictionary<int, FieldContract> seen, Dictionary<int, List<string>> overlapped)
    {
        if (field.InstanceOffset is not int offset)
            return;
        if (seen.TryGetValue(offset, out var existing))
        {
            if (!type.IsExplicitLayout)
            {
                throw new InvalidOperationException(
                    "Two fields share instance offset 0x" + offset.ToString("X") + " on " + Describe(type) +
                    ". First: " + existing.OwnerType + "::" + existing.Name +
                    ". Second: " + field.OwnerType + "::" + field.Name + ".");
            }
            if (!overlapped.TryGetValue(offset, out var names))
                overlapped[offset] = names = [existing.OwnerType + "::" + existing.Name];
            names.Add(field.OwnerType + "::" + field.Name);
            return;
        }
        seen.Add(offset, field);
        fields.Add(field);
    }

    private static void AddStaticFields(EngineTypeInfo type, string display, List<FieldContract> fields, Dictionary<int, string> notes, int id, Dictionary<int, EngineTypeInfo> byId)
    {
        var seen = new Dictionary<int, FieldContract>();
        foreach (var field in type.Fields)
        {
            if (!field.IsStatic || field.IsLiteral)
                continue;
            if (field.Offset is null or < 0)
            {
                if (field.Offset is < 0)
                    Note(notes, id, "Thread-static field " + display + "::" + field.Name + " has no static-block offset and is not resolved.");
                continue;
            }
            if (!Liftable(field, byId, out var kind))
            {
                Note(notes, id, "Static field " + display + "::" + field.Name + " (" + field.Type + ") is outside the lifter subset.");
                continue;
            }
            var contract = new FieldContract
            {
                Name = field.Name,
                Type = kind,
                DeclaredType = field.Type,
                DeclaredTypeId = DefinitionId(field.TypeReference),
                Access = field.Access,
                IsStatic = true,
                StaticOffset = field.Offset,
                OwnerType = display,
                OwnerTypeId = type.MetadataIndex
            };
            if (seen.TryGetValue(field.Offset.Value, out var existing))
            {
                throw new InvalidOperationException(
                    "Two static fields share static offset 0x" + field.Offset.Value.ToString("X") + " on " + Describe(type) +
                    ". First: " + existing.Name + ". Second: " + field.Name + ".");
            }
            seen.Add(field.Offset.Value, contract);
            fields.Add(contract);
        }
    }

    private static int? DefinitionId(TypeRef? type) =>
        type is { Kind: TypeRefKind.Definition or TypeRefKind.GenericInstance, DefinitionIndex: int id } ? id : null;

    private static bool Liftable(EngineFieldInfo field, Dictionary<int, EngineTypeInfo> byId, out PrimitiveKind kind)
    {
        kind = PrimitiveKind.Reference;
        var type = field.TypeReference;
        if (type is { Kind: TypeRefKind.Pointer or TypeRefKind.ByRef or TypeRefKind.GenericParameter or TypeRefKind.Unresolved })
            return false;
        if (type is { Kind: TypeRefKind.Array })
            return true;
        if (AnalysisPipeline.TryPrimitive(field.Type, out kind))
            return true;
        kind = PrimitiveKind.Reference;
        if (type?.DefinitionIndex is not int id || !byId.TryGetValue(id, out var info))
            return false;
        if (info.IsEnum || info.IsValueType)
            return false;
        return true;
    }
}

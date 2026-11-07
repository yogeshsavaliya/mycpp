using Il2CppRecover.EngineAdapter;
using Il2CppRecover.Recovery;

namespace Il2CppRecover.Analysis;

public static class OutputWriter
{
    public static void Write(string outputDirectory, InventoryResult inventory, MetadataCatalog? catalog, IReadOnlyList<MethodRecord> methods, RecoveryReport report, IReadOnlyList<string> logs, string? engineDiagnostic, IReadOnlyList<EngineTypeInfo>? types = null, IReadOnlyList<EngineMethod>? engineMethods = null)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "inventory.json"), JsonDefaults.Serialize(inventory));
        File.WriteAllText(Path.Combine(outputDirectory, "metadata-catalog.json"), JsonDefaults.Serialize(catalog));
        File.WriteAllText(Path.Combine(outputDirectory, "methods.json"), JsonDefaults.Serialize(methods));
        File.WriteAllText(Path.Combine(outputDirectory, "recovery-report.json"), JsonDefaults.Serialize(report));
        File.WriteAllText(Path.Combine(outputDirectory, "logs.txt"), string.Join(Environment.NewLine, logs) + Environment.NewLine);
        var map = methods.Select(m => new
        {
            m.Assembly,
            m.Namespace,
            m.DeclaringType,
            m.Name,
            m.Signature,
            m.MetadataToken,
            m.NativeVirtualAddress,
            m.NativeFileOffset,
            boundary = m.BoundaryEvidence.ToString(),
            status = m.Status.ToString(),
            m.SharedImplementationCount
        });
        File.WriteAllText(Path.Combine(outputDirectory, "native-map.json"), JsonDefaults.Serialize(map));
        var manifest = new
        {
            tool = ToolInfo.Name,
            version = ToolInfo.Version,
            inventory.InputSha256,
            inventory.InputSizeBytes,
            cpp2ilCommit = ToolInfo.Cpp2IlCommit,
            cpp2ilCore = ToolInfo.Cpp2IlCoreVersion,
            libCpp2il = ToolInfo.LibCpp2IlVersion,
            disarm = ToolInfo.DisarmVersion,
            engineDiagnostic,
            note = "Extracted files are copies. The input archive was opened read-only."
        };
        File.WriteAllText(Path.Combine(outputDirectory, "input-manifest.json"), JsonDefaults.Serialize(manifest));

        var codeDir = Path.Combine(outputDirectory, "csharp");
        if (Directory.Exists(codeDir))
            Directory.Delete(codeDir, recursive: true);
        Directory.CreateDirectory(codeDir);
        var catalogIndexes = new HashSet<int>(catalog?.Images.SelectMany(i => i.Types).Select(t => t.MetadataIndex) ?? []);
        if (catalog is not null)
            WriteCatalogSources(codeDir, catalog, methods, types, engineMethods);
        foreach (var group in methods.Where(m => m.TypeMetadataIndex is not int index || !catalogIndexes.Contains(index)).GroupBy(m => m.Assembly + "|" + m.Namespace + "|" + m.DeclaringType))
        {
            var first = group.First();
            var matches = (types ?? []).Where(t => t.Image == first.Assembly && t.Namespace == first.Namespace && t.Name == first.DeclaringType).ToList();
            var facts = matches.Count == 1 ? matches[0] : null;
            var declarationFields = (facts?.Fields ?? []).Select(field => new FieldContract
            {
                Name = field.Name,
                Type = PrimitiveKind.Int32,
                DeclaredType = field.Type,
                Access = field.Access,
                IsStatic = field.IsStatic,
                InstanceOffset = field.Offset
            }).ToList();
            var bodies = new List<(MethodContract Contract, string Body, bool Placeholder)>();
            foreach (var method in group)
            {
                bodies.Add((
                    new MethodContract
                    {
                        Name = method.Name,
                        Namespace = method.Namespace,
                        DeclaringType = method.DeclaringType,
                        Assembly = method.Assembly,
                        IsStatic = method.IsStatic,
                        IsAbstract = method.MethodKind == MethodKind.Abstract,
                        IsVirtual = method.IsVirtual,
                        Access = string.IsNullOrWhiteSpace(method.Access) ? "unresolved" : method.Access,
                        DeclaredReturnType = string.IsNullOrWhiteSpace(method.ReturnTypeName) ? "unresolved" : method.ReturnTypeName,
                        Parameters = DeclaredParameters(method, null, [], []),
                        Fields = bodies.Count == 0 ? declarationFields : []
                    },
                    method.Status == MethodRecoveryStatus.RecoveredUnvalidated && method.CSharp is not null
                        ? method.CSharp
                        : method.CSharp ?? "// No C# was emitted for this method.",
                    method.CSharpIsPlaceholder || method.Status != MethodRecoveryStatus.RecoveredUnvalidated));
            }
            var baseType = facts?.BaseType;
            if (baseType is "System.Object" or "System.ValueType")
                baseType = null;
            var text = CSharpEmitter.EmitType(first.Namespace, first.DeclaringType, bodies, baseType, facts?.IsValueType ?? false, facts?.IsEnum ?? false, facts?.Events ?? [], facts?.DeclaringType);
            var safeAssembly = Sanitize(first.Assembly);
            var directory = Path.Combine(codeDir, safeAssembly, string.IsNullOrEmpty(first.Namespace) ? "_" : Sanitize(first.Namespace));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, Sanitize(first.DeclaringType) + ".cs"), text);
        }

        File.WriteAllText(Path.Combine(outputDirectory, "RECOVERY_NOTES.txt"), string.Join(Environment.NewLine, report.Limitations) + Environment.NewLine + report.Counts.Denominator + Environment.NewLine + report.BehaviouralValidation + Environment.NewLine);
    }

    public static void WriteSlice(string outputDirectory, MethodContract contract, MethodBodyResult result)
    {
        Directory.CreateDirectory(outputDirectory);
        var record = new MethodRecord
        {
            Assembly = contract.Assembly,
            Namespace = contract.Namespace,
            DeclaringType = contract.DeclaringType,
            Name = contract.Name,
            Signature = contract.ReturnType + " " + contract.Name,
            IsStatic = contract.IsStatic,
            MethodKind = contract.IsGeneric ? MethodKind.Generic : MethodKind.Concrete,
            Status = result.Status,
            Diagnostics = result.Diagnostics,
            Instructions = result.Instructions,
            CSharp = result.CSharp,
            CSharpIsPlaceholder = result.CSharpIsPlaceholder,
            CandidateCSharp = result.CandidateCSharp,
            Provenance = result.Provenance,
            BoundaryEvidence = BoundaryEvidence.CallerSuppliedSlice
        };
        File.WriteAllText(Path.Combine(outputDirectory, "method.json"), JsonDefaults.Serialize(record));
        if (result.CSharp is not null && !result.CSharpIsPlaceholder)
        {
            var text = CSharpEmitter.EmitType(contract.Namespace, contract.DeclaringType, [(contract, result.CSharp, false)]);
            File.WriteAllText(Path.Combine(outputDirectory, contract.DeclaringType + ".cs"), text);
        }
    }

    private static Dictionary<int, T> IndexTypes<T>(IEnumerable<T> items, Func<T, int> index, Func<T, string> describe)
    {
        var map = new Dictionary<int, T>();
        foreach (var item in items)
        {
            var key = index(item);
            if (key < 0)
                continue;
            if (map.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    "Two type records share metadata index " + key +
                    ". First: " + describe(existing) +
                    ". Second: " + describe(item) + ".");
            }
            map.Add(key, item);
        }
        return map;
    }

    /// <summary>
    /// One file per top-level catalog type. Nested types stay inside their parent.
    /// Every metadata type is kept, including each assembly's &lt;Module&gt;, which is
    /// written as a module section rather than as class &lt;Module&gt;.
    /// A path shared by two type ids gets the id in the file name before anything is written.
    /// </summary>
    private static void WriteCatalogSources(string codeDir, MetadataCatalog catalog, IReadOnlyList<MethodRecord> methods, IReadOnlyList<EngineTypeInfo>? types, IReadOnlyList<EngineMethod>? nativeMethods)
    {
        var factsByIndex = IndexTypes(types ?? [], t => t.MetadataIndex, t => t.Image + " " + t.Namespace + "." + t.Name);
        var engineMethods = IndexTypes((nativeMethods ?? []).Where(m => m.MetadataIndex >= 0), m => m.MetadataIndex, m => m.Image + " " + m.DeclaringType + "::" + m.Name);
        var names = new Dictionary<string, List<(string Image, int Id)>>(StringComparer.Ordinal);
        foreach (var type in types ?? [])
        {
            var key = string.IsNullOrEmpty(type.DisplayName) ? type.Name : type.DisplayName;
            if (!names.TryGetValue(key, out var list))
                names[key] = list = [];
            list.Add((type.Image, type.MetadataIndex));
        }
        var methodsByType = methods.Where(m => m.TypeMetadataIndex is int).GroupBy(m => m.TypeMetadataIndex!.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var image in catalog.Images)
        {
            var byIndex = IndexTypes(image.Types, t => t.MetadataIndex, t => image.Name + " " + t.Namespace + "." + t.Name);
            var declarations = new Dictionary<int, CSharpEmitter.TypeDeclaration>();
            foreach (var type in image.Types)
            {
                factsByIndex.TryGetValue(type.MetadataIndex, out var facts);
                methodsByType.TryGetValue(type.MetadataIndex, out var typeMethods);
                declarations[type.MetadataIndex] = Declaration(type, facts, typeMethods ?? [], engineMethods, names);
            }
            foreach (var type in image.Types)
            {
                if (type.Name == "<Module>")
                    continue;
                if (type.DeclaringTypeIndex is int parent && declarations.TryGetValue(parent, out var outer))
                    outer.Nested.Add(declarations[type.MetadataIndex]);
            }
            var planned = new List<(CatalogType Type, string Directory, string Path)>();
            foreach (var type in image.Types)
            {
                if (type.DeclaringTypeIndex is int parent && byIndex.ContainsKey(parent) && type.Name != "<Module>")
                    continue;
                var directory = Path.Combine(codeDir, Sanitize(image.Name), string.IsNullOrEmpty(type.Namespace) ? "_" : Sanitize(type.Namespace));
                var stem = type.Name == "<Module>" ? "_Module_" + type.MetadataIndex : Sanitize(type.Name);
                planned.Add((type, directory, Path.Combine(directory, stem + ".cs")));
            }
            var pathCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in planned)
                pathCounts[item.Path] = pathCounts.TryGetValue(item.Path, out var count) ? count + 1 : 1;
            foreach (var item in planned)
            {
                Directory.CreateDirectory(item.Directory);
                var shared = pathCounts[item.Path] > 1;
                var declaration = declarations[item.Type.MetadataIndex];
                if (shared)
                    declaration.DisambiguateName = true;
                var path = shared
                    ? Path.Combine(item.Directory, Sanitize(item.Type.Name) + "_" + item.Type.MetadataIndex + ".cs")
                    : item.Path;
                var text = item.Type.Name == "<Module>"
                    ? CSharpEmitter.EmitModule(image.Name, declaration)
                    : CSharpEmitter.EmitFile(item.Type.Namespace, declaration);
                File.WriteAllText(path, text);
            }
        }
    }

    private static CSharpEmitter.TypeDeclaration Declaration(CatalogType type, EngineTypeInfo? facts, List<MethodRecord> typeMethods, IReadOnlyDictionary<int, EngineMethod> engineMethods, Dictionary<string, List<(string Image, int Id)>> names)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var fields = (facts?.Fields ?? []).Where(f => facts is not { IsEnum: true } || (!f.IsLiteral && f.Name != "value__")).Select(field => new FieldContract
        {
            Name = field.Name,
            Type = PrimitiveKind.Int32,
            DeclaredType = Present(field.Type, field.TypeReference, names, aliases),
            Access = field.Access,
            IsStatic = field.IsStatic,
            InstanceOffset = field.IsStatic ? null : field.Offset
        }).ToList();
        IReadOnlyList<(string, string)>? members = null;
        string? underlying = null;
        if (facts is { IsEnum: true })
        {
            underlying = facts.Fields.FirstOrDefault(f => f.Name == "value__")?.Type;
            var literals = facts.Fields.Where(f => f.IsLiteral).ToList();
            if (literals.Count > 0 && literals.All(f => f.ConstantValue is not null))
                members = literals.Select(f => (f.Name, EnumLiteral(f.ConstantValue!))).ToList();
        }
        var bodies = typeMethods.Select(method =>
        {
            EngineMethod? native = null;
            if (method.MetadataMethodIndex is int methodIndex)
                engineMethods.TryGetValue(methodIndex, out native);
            return (
            new MethodContract
            {
                Name = method.Name,
                Namespace = method.Namespace,
                DeclaringType = method.DeclaringType,
                Assembly = method.Assembly,
                IsStatic = method.IsStatic,
                IsAbstract = method.MethodKind == MethodKind.Abstract,
                IsVirtual = method.IsVirtual,
                Access = string.IsNullOrWhiteSpace(method.Access) ? "unresolved" : method.Access,
                DeclaredReturnType = Present(string.IsNullOrWhiteSpace(method.ReturnTypeName) ? "unresolved" : method.ReturnTypeName, native?.ReturnTypeRef, names, aliases),
                Parameters = DeclaredParameters(method, native, names, aliases)
            },
            method.CSharp ?? "// No C# was emitted for this method.",
            method.CSharpIsPlaceholder || method.Status != MethodRecoveryStatus.RecoveredUnvalidated);
        }).ToList();
        var baseType = facts?.BaseType;
        if (baseType is "System.Object" or "System.ValueType" or "System.Enum")
            baseType = null;
        else if (baseType is not null)
            baseType = Present(baseType, facts?.BaseTypeRef, names, aliases);
        var declaration = new CSharpEmitter.TypeDeclaration
        {
            Name = type.Name,
            TypeId = type.MetadataIndex,
            BaseType = baseType,
            IsValueType = facts?.IsValueType ?? type.IsValueType,
            IsEnum = facts?.IsEnum ?? type.IsEnum,
            EnumUnderlyingType = underlying,
            EnumMembers = members,
            Events = facts?.Events ?? [],
            Fields = fields,
            Methods = bodies
        };
        foreach (var alias in aliases)
            declaration.ExternAliases.Add(alias);
        return declaration;
    }

    private static string Present(string display, TypeRef? reference, Dictionary<string, List<(string Image, int Id)>> names, HashSet<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(display) || display == "unresolved" || !names.TryGetValue(display, out var hits) || hits.Count < 2)
            return display;
        var id = reference is { Kind: TypeRefKind.Definition or TypeRefKind.GenericInstance, DefinitionIndex: int value } ? value : (int?)null;
        if (id is int known)
        {
            foreach (var hit in hits)
            {
                if (hit.Id != known)
                    continue;
                var alias = AssemblyAlias(hit.Image);
                aliases.Add(alias);
                return alias + "::" + display;
            }
        }
        return "/* ambiguous across assemblies: " + string.Join(", ", hits.Select(hit => hit.Image + " index " + hit.Id)) + " */ object";
    }

    private static string AssemblyAlias(string image)
    {
        var stem = image.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
        var chars = stem.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var text = new string(chars);
        if (text.Length == 0 || !char.IsLetter(text[0]))
            text = "A" + text;
        return text;
    }

    private static string EnumLiteral(object value) => value switch
    {
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "0"
    };

    private static IReadOnlyList<ParameterContract> DeclaredParameters(MethodRecord method, EngineMethod? native, Dictionary<string, List<(string Image, int Id)>> names, HashSet<string> aliases)
    {
        var count = Math.Max(method.ParameterNames.Count, method.ParameterTypeNames.Count);
        var list = new List<ParameterContract>(count);
        for (var i = 0; i < count; i++)
        {
            var name = i < method.ParameterNames.Count && !string.IsNullOrWhiteSpace(method.ParameterNames[i])
                ? method.ParameterNames[i]
                : "param" + i;
            var typeName = i < method.ParameterTypeNames.Count && !string.IsNullOrWhiteSpace(method.ParameterTypeNames[i])
                ? method.ParameterTypeNames[i]
                : "unresolved";
            var reference = native is not null && i < native.ParameterTypeRefs.Count ? native.ParameterTypeRefs[i] : null;
            list.Add(new ParameterContract
            {
                Name = name,
                Type = PrimitiveKind.Int32,
                DeclaredType = Present(typeName, reference, names, aliases)
            });
        }
        return list;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray();
        var text = new string(chars);
        return text.Length == 0 ? "_" : text;
    }
}

using Il2CppRecover.Recovery;

namespace Il2CppRecover.Analysis;

public static class OutputWriter
{
    public static void Write(string outputDirectory, InventoryResult inventory, MetadataCatalog? catalog, IReadOnlyList<MethodRecord> methods, RecoveryReport report, IReadOnlyList<string> logs, string? engineDiagnostic)
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
        foreach (var group in methods.GroupBy(m => m.Assembly + "|" + m.Namespace + "|" + m.DeclaringType))
        {
            var first = group.First();
            var bodies = group.Select(m => (
                Contract: new MethodContract
                {
                    Name = m.Name,
                    Namespace = m.Namespace,
                    DeclaringType = m.DeclaringType,
                    Assembly = m.Assembly,
                    IsStatic = m.IsStatic,
                    ReturnType = GuessReturn(m),
                    Parameters = GuessParameters(m)
                },
                Body: m.Status == MethodRecoveryStatus.RecoveredUnvalidated && m.CSharp is not null
                    ? m.CSharp
                    : m.CSharp ?? "// No C# was emitted for this method.",
                Placeholder: m.CSharpIsPlaceholder || m.Status != MethodRecoveryStatus.RecoveredUnvalidated
            )).ToList();
            var text = CSharpEmitter.EmitType(first.Namespace, first.DeclaringType, bodies);
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

    private static PrimitiveKind GuessReturn(MethodRecord method)
    {
        var text = method.Signature;
        if (text.Contains("Boolean", StringComparison.Ordinal) || text.Contains("bool", StringComparison.Ordinal))
            return PrimitiveKind.Boolean;
        if (text.Contains("UInt32", StringComparison.Ordinal) || text.Contains("uint", StringComparison.Ordinal))
            return PrimitiveKind.UInt32;
        if (text.Contains("Int64", StringComparison.Ordinal) || text.Contains("long", StringComparison.Ordinal))
            return PrimitiveKind.Int64;
        if (text.Contains("Void", StringComparison.Ordinal) || text.StartsWith("void", StringComparison.Ordinal) || text.Contains(" static void", StringComparison.Ordinal))
            return PrimitiveKind.Void;
        return PrimitiveKind.Int32;
    }

    private static IReadOnlyList<ParameterContract> GuessParameters(MethodRecord method)
    {
        var open = method.Signature.IndexOf('(');
        var close = method.Signature.LastIndexOf(')');
        if (open < 0 || close <= open)
            return [];
        var inside = method.Signature[(open + 1)..close];
        if (string.IsNullOrWhiteSpace(inside))
            return [];
        var list = new List<ParameterContract>();
        foreach (var part in inside.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = bits.Length == 0 ? "value" : bits[^1];
            var typeText = bits.Length < 2 ? "int" : bits[0];
            AnalysisPipeline.TryPrimitive(typeText, out var type);
            if (type == PrimitiveKind.Reference)
                type = PrimitiveKind.Int32;
            list.Add(new ParameterContract { Name = name, Type = type });
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

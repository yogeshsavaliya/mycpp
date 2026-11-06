namespace Il2CppRecover.Analysis;

public static class RecoveryReportBuilder
{
    public static RecoveryReport Build(IReadOnlyList<MethodRecord> methods, string bodyScope = "full")
    {
        var unique = methods
            .Where(m => !string.IsNullOrEmpty(m.NativeVirtualAddress))
            .GroupBy(m => m.NativeVirtualAddress)
            .ToList();
        var fully = unique.Count(g => g.Any(m => m.Status == MethodRecoveryStatus.RecoveredUnvalidated && !m.CSharpIsPlaceholder));
        return new RecoveryReport
        {
            ToolName = ToolInfo.Name,
            ToolVersion = ToolInfo.Version,
            RecoveryEngine = ToolInfo.RecoveryEngine,
            RecoveryEngineVersion = ToolInfo.RecoveryEngineVersion,
            PinnedCpp2IlCommit = ToolInfo.Cpp2IlCommit,
            PinnedCpp2IlCore = ToolInfo.Cpp2IlCorePackage + " " + ToolInfo.Cpp2IlCoreVersion,
            PinnedLibCpp2Il = ToolInfo.LibCpp2IlPackage + " " + ToolInfo.LibCpp2IlVersion,
            PinnedDisarm = ToolInfo.DisarmPackage + " " + ToolInfo.DisarmVersion,
            BuildIdentity = ToolInfo.BuildIdentity,
            BodyAnalysisScope = bodyScope,
            Breakdown =
            [
                Breakdown("Gameplay and third-party assemblies", methods.Where(m => !IsFramework(m)).ToList()),
                Breakdown("Framework assemblies (mscorlib, System*, Mono.*, UnityEngine*, Unity.*, Microsoft.*, netstandard)", methods.Where(IsFramework).ToList()),
                Breakdown("All assemblies", methods.ToList())
            ],
            Counts = new RecoveryCounts
            {
                MethodsInMetadata = methods.Count,
                MethodsWithNativeAddress = methods.Count(m => !string.IsNullOrEmpty(m.NativeVirtualAddress)),
                UniqueNativeAddresses = unique.Count,
                AbstractOrExternalMethods = methods.Count(m => m.MethodKind is MethodKind.Abstract or MethodKind.External),
                MethodsSharingANativeAddress = unique.Where(g => g.Count() > 1).Sum(g => g.Count()),
                FullyRecoveredUniqueBodies = fully,
                PartiallyRecovered = methods.Count(m => m.Status == MethodRecoveryStatus.PartiallyRecovered),
                DisassembledOnly = methods.Count(m => m.Status == MethodRecoveryStatus.Disassembled),
                MetadataOnly = methods.Count(m => m.Status == MethodRecoveryStatus.MetadataOnly),
                Unsupported = methods.Count(m => m.Status == MethodRecoveryStatus.Unsupported),
                Failed = methods.Count(m => m.Status == MethodRecoveryStatus.Failed),
                PlaceholderBodies = methods.Count(m => m.CSharpIsPlaceholder),
                AnalysisBudgetExceeded = methods.Count(m => m.Status == MethodRecoveryStatus.AnalysisBudgetExceeded),
                TimedOut = methods.Count(m => m.Status == MethodRecoveryStatus.TimedOut),
                NotAnalyzed = methods.Count(m => m.Status == MethodRecoveryStatus.NotAnalyzed),
                CompilationChecked = methods.Count(m => m.Compilation == "Compiled"),
                BehaviourCheckedOnFixtures = methods.Count(m => m.Behaviour == "CheckedOnFixture"),
                Denominator = "FullyRecoveredUniqueBodies counts distinct native addresses whose lift status is RecoveredUnvalidated. Abstract methods, external methods, metadata-only methods, placeholders, and extra metadata rows that share one address are not added to that numerator."
            },
            BehaviouralValidation = "No behavioural validation was run for this input. Passing a synthetic fixture does not certify an arbitrary APK. Compilation of a placeholder is not behavioural equivalence.",
            Limitations =
            [
                "Cpp2IL " + ToolInfo.Cpp2IlTag + " (" + ToolInfo.Cpp2IlCommit + ") is used only to load metadata and locate method pointers. Cpp2IL.Core analysis is not invoked. Method bodies come from the structured ARM64 lifter.",
                "A body is emitted only when recursive descent from the method entry is closed (RET, a tail call to a known method entry, or a call with noreturn evidence) and every instruction on every path was lifted. Otherwise the method keeps a placeholder. A candidate may be stored in methods.json and is not written as the method body.",
                "Not lifted: loops other than one legacy while shape, switches and jump tables, virtual and interface calls, delegates, generic method instances, struct parameters and returns, enum-typed values in bodies, static fields, array element access, string literals, object allocation, exceptions other than an identified null-reference raise, and division.",
                "The null-reference raise helper is identified from call-site structure across the binary. When that evidence is insufficient, bodies that reach it stay candidates.",
                "RecoveredUnvalidated means the lifted shape compiled from closed control flow. It has not been compiled against Unity assemblies or compared with the game's behaviour unless a later pass says so.",
                "Scene, prefab, asset, and script-reference restoration are not implemented. The C# export is an analysis view, not a Unity project.",
                bodyScope == "gameplay-first"
                    ? "Gameplay-first analysis left framework bodies unanalyzed. They stay in the metadata and the call map, and they are not counted as recovered."
                    : "Full analysis attempts a body for every concrete method. A method that exceeds its budget is recorded as AnalysisBudgetExceeded or TimedOut and is not counted as recovered."
            ]
        };
    }

    private static readonly string[] FrameworkPrefixes = ["mscorlib", "netstandard", "System", "Mono.", "UnityEngine", "Unity.", "Microsoft."];

    public static bool IsFrameworkAssembly(string assembly) =>
        FrameworkPrefixes.Any(p => assembly.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsFramework(MethodRecord method) => IsFrameworkAssembly(method.Assembly);

    private static StageBreakdown Breakdown(string scope, List<MethodRecord> methods) => new()
    {
        Scope = scope,
        Assemblies = methods.Select(m => m.Assembly).Distinct().Order().ToList(),
        MethodsDiscovered = methods.Count,
        MappedToNativeCode = methods.Count(m => !string.IsNullOrEmpty(m.NativeVirtualAddress)),
        Disassembled = methods.Count(m => m.Instructions.Count > 0),
        CandidateBodies = methods.Count(m => m.CandidateCSharp is not null || m.Status == MethodRecoveryStatus.RecoveredUnvalidated && !m.CSharpIsPlaceholder),
        EmittedWithoutPlaceholder = methods.Count(m => m.CSharp is not null && !m.CSharpIsPlaceholder),
        PassedCompilationCheck = methods.Count(m => m.Compilation == "Compiled"),
        PassedBehaviouralCheck = methods.Count(m => m.Behaviour == "CheckedOnFixture"),
        UnrecoveredByReason = methods
            .Where(m => m.CSharp is null || m.CSharpIsPlaceholder)
            .GroupBy(Reason)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count())
    };

    public static string Reason(MethodRecord m)
    {
        if (m.MethodKind is MethodKind.Abstract or MethodKind.External)
            return "abstract or external (no body expected)";
        if (m.MethodKind == MethodKind.Generic)
            return "generic method";
        if (m.Status == MethodRecoveryStatus.NotAnalyzed)
            return "gameplay-first: framework body not analyzed";
        if (m.Status == MethodRecoveryStatus.AnalysisBudgetExceeded)
            return "analysis budget exceeded";
        if (m.Status == MethodRecoveryStatus.TimedOut)
            return "method deadline exceeded";
        var text = string.Join(" ", m.Diagnostics);
        (string Key, string Reason)[] table =
        [
            ("Gameplay-first analysis left", "gameplay-first: framework body not analyzed"),
            ("AnalysisBudgetExceeded", "analysis budget exceeded"),
            ("TimedOut:", "method deadline exceeded"),
            ("No native virtual address", "no native address"),
            ("Instance method of a value type", "value-type instance method"),
            ("value type", "struct parameter, return, or field type"),
            ("enum type", "enum-typed value"),
            ("not found in the loaded type table", "unknown parameter/return type"),
            ("not resolved from the native type table", "unresolved parameter/return type"),
            ("indirect call", "virtual/interface/delegate call"),
            ("object header", "virtual dispatch or type check"),
            ("global data", "static field or runtime metadata access"),
            ("not in the method map", "call to unidentified native code"),
            ("generic", "generic method or instance"),
            ("legacy single-loop", "loop"),
            ("indirect branch", "switch / jump table"),
            ("noreturn call to", "unidentified noreturn helper (candidate only)"),
            ("no field of", "field not in layout"),
            ("non-constant index", "array or computed address"),
            ("stack slot", "stack-passed arguments or spills"),
            ("falls through past", "boundary not closed"),
            ("outside the slice", "branch outside the method"),
            ("is not in the lifted subset", "instruction not lifted"),
            ("is not lifted", "operation not lifted")
        ];
        foreach (var (key, reason) in table)
        {
            if (text.Contains(key, StringComparison.Ordinal))
                return reason;
        }
        return "other";
    }
}

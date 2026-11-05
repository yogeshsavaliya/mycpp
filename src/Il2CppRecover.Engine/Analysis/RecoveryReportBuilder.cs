namespace Il2CppRecover.Analysis;

public static class RecoveryReportBuilder
{
    public static RecoveryReport Build(IReadOnlyList<MethodRecord> methods)
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
                CompilationChecked = methods.Count(m => m.Compilation == "Compiled"),
                BehaviourCheckedOnFixtures = methods.Count(m => m.Behaviour == "CheckedOnFixture"),
                Denominator = "FullyRecoveredUniqueBodies counts distinct native addresses whose lift status is RecoveredUnvalidated. Abstract methods, external methods, metadata-only methods, placeholders, and extra metadata rows that share one address are not added to that numerator."
            },
            BehaviouralValidation = "No behavioural validation was run for this input. Passing a synthetic fixture does not certify an arbitrary APK. Compilation of a placeholder is not behavioural equivalence.",
            Limitations =
            [
                "Cpp2IL " + ToolInfo.Cpp2IlTag + " (" + ToolInfo.Cpp2IlCommit + ") is used to load metadata and, when registration structures are present, to locate method pointers. Its dll_il_recovery writer emits ldnull/throw stubs and is not used as recovered logic.",
                "The ARM64 lifter covers constants, integer arithmetic, comparisons, if/else returns, a single while-loop shape, direct calls with a known target, and field accesses whose offset or absolute address was supplied by metadata-backed layout. It does not cover floating point, switches, exceptions, generics, or delegates.",
                "A slice bounded only by the next method address is reported as partially recovered even if a candidate body was lifted.",
                "Scene, prefab, asset, and script-reference restoration are not implemented. The C# export is an analysis view, not a Unity project."
            ]
        };
    }
}

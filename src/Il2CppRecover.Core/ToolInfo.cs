namespace Il2CppRecover;

/// <summary>
/// Versions recorded in every report. Package versions are the ones restored
/// and audited for this tree, not floating latest tags.
/// </summary>
public static class ToolInfo
{
    public const string Name = "Il2CppRecover";
    public const string Version = "0.4.0";

    public const string Cpp2IlRepository = "https://github.com/SamboyCoding/Cpp2IL";
    public const string Cpp2IlCommit = "12c8b4413900558a14e30adc4fc4d13d82383901";
    public const string Cpp2IlTag = "2022.1.0-pre-release.21";
    public const string LibCpp2IlPackage = "Samboy063.LibCpp2IL";
    public const string LibCpp2IlVersion = "2022.1.0-pre-release.21";
    public const string Cpp2IlCorePackage = "Samboy063.Cpp2IL.Core";
    public const string Cpp2IlCoreVersion = "2022.1.0-pre-release.21";
    public const string DisarmPackage = "Disarm";
    public const string DisarmVersion = "2022.1.0-master.99";

    public const string RecoveryEngine = "Il2CppRecover.StructuredArm64Lifter";
    public const string RecoveryEngineVersion = "0.4.0";

    /// <summary>Version plus the git commit when the build could read one. Older reports do not have this value.</summary>
    public static string BuildIdentity { get; } = ReadBuildIdentity();

    private static string ReadBuildIdentity()
    {
        var info = typeof(ToolInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? Version;
        return info.Contains('+') ? info : info + "+unknown-commit";
    }
}

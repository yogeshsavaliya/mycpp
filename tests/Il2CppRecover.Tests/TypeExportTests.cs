using Il2CppRecover.EngineAdapter;
using Il2CppRecover.Recovery;

namespace Il2CppRecover.Tests;

public class TypeExportTests
{
    [Fact]
    public void Resolved_types_are_kept_when_the_body_cannot_be_lifted()
    {
        var diagnostics = new List<string>();
        var method = new CatalogMethod
        {
            Name = "Run",
            Parameters =
            [
                new CatalogParameter { Name = "duration", Type = "unresolved" },
                new CatalogParameter { Name = "power", Type = "unresolved" }
            ],
            ReturnType = "unresolved"
        };
        var native = new EngineMethod
        {
            Name = "Run",
            DeclaringType = "Ticker",
            Namespace = "Sample",
            Image = "Assembly-CSharp.dll",
            ReturnType = "System.Collections.IEnumerator",
            ParameterTypes = ["System.Single", "System.Int32"]
        };

        var returnType = AnalysisPipeline.ResolveDeclaredTypes(method, native, diagnostics, out var parameterTypes);
        Assert.Equal("System.Collections.IEnumerator", returnType);
        Assert.Equal(["System.Single", "System.Int32"], parameterTypes);
        Assert.Empty(diagnostics);

        var root = Path.Combine(Path.GetTempPath(), "il2cpp-types-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var record = Record("Run", returnType, ["duration", "power"], parameterTypes.ToArray(), false, MethodRecoveryStatus.Disassembled,
            ["Return type 'System.Collections.IEnumerator' is outside the lifter subset, so no body was invented."]);
        var voidRecord = Record("Tick", "System.Void", [], [], false, MethodRecoveryStatus.Unsupported,
            ["The slice contains a branch whose target is outside the slice. The boundary was not guessed."]);
        var missing = Record("Missing", "unresolved", ["value"], ["unresolved"], true, MethodRecoveryStatus.MetadataOnly,
            ["No native virtual address was located. Metadata names are not recovered method logic."]);
        OutputWriter.Write(root, Inventory(), null, [record, voidRecord, missing], Report(), [], null);

        var csharp = File.ReadAllText(Path.Combine(root, "csharp", "Assembly-CSharp.dll", "Sample", "Ticker.cs"));
        Assert.Contains("public System.Collections.IEnumerator Run(float duration, int power)", csharp);
        Assert.Contains("public void Tick()", csharp);
        Assert.Contains("public static unresolved Missing(unresolved value)", csharp);
        Assert.Contains("was not replaced with int", csharp);
        Assert.Contains("IL2CPP_RECOVER_UNRECOVERED", csharp);
        Assert.Contains("System.Collections.IEnumerator", csharp);
        Assert.DoesNotContain("int Run", csharp);
        Assert.DoesNotContain("int duration", csharp);
        Assert.DoesNotContain("int Tick", csharp);
        Assert.DoesNotContain("int Missing", csharp);
        Assert.DoesNotContain("return 0;", csharp);
        Assert.DoesNotContain("return null;", csharp);
    }

    [Fact]
    public void Mismatched_parameter_counts_are_not_filled_with_int()
    {
        var diagnostics = new List<string>();
        var method = new CatalogMethod
        {
            Name = "Run",
            Parameters = [new CatalogParameter { Name = "duration", Type = "unresolved" }],
            ReturnType = "unresolved"
        };
        var native = new EngineMethod
        {
            Name = "Run",
            DeclaringType = "Ticker",
            Namespace = "Sample",
            Image = "Assembly-CSharp.dll",
            ReturnType = "System.Void",
            ParameterTypes = ["System.Single", "System.Single"]
        };
        var returnType = AnalysisPipeline.ResolveDeclaredTypes(method, native, diagnostics, out var parameterTypes);
        Assert.Equal("System.Void", returnType);
        Assert.Equal(["unresolved"], parameterTypes);
        Assert.Contains(diagnostics, d => d.Contains("were not replaced with int", StringComparison.Ordinal));
    }

    [Fact]
    public void Byref_and_primitive_aliases_keep_the_resolved_type()
    {
        Assert.Equal("void", CSharpEmitter.FormatDeclaredType("System.Void"));
        Assert.Equal("float", CSharpEmitter.FormatDeclaredType("System.Single"));
        Assert.Equal("ref float", CSharpEmitter.FormatDeclaredType("System.Single&"));
        Assert.Equal("System.Collections.IEnumerator", CSharpEmitter.FormatDeclaredType("System.Collections.IEnumerator"));
        Assert.Equal("int[]", CSharpEmitter.FormatDeclaredType("System.Int32[]"));
        Assert.Equal("unresolved", CSharpEmitter.FormatDeclaredType("unresolved"));
        Assert.Equal("unresolved", CSharpEmitter.FormatDeclaredType("  "));
        Assert.Equal("byte*", CSharpEmitter.FormatDeclaredType("System.Byte*"));
        Assert.Equal("byte[]", CSharpEmitter.FormatDeclaredType("System.Byte[]"));
        Assert.Equal("void*", CSharpEmitter.FormatDeclaredType("System.Void*"));
        Assert.Equal("Unity.Collections.NativeArray<byte>", CSharpEmitter.FormatDeclaredType("Unity.Collections.NativeArray`1<System.Byte>"));
    }

    [Fact]
    public void Constructors_keep_their_metadata_name_and_do_not_become_int_methods()
    {
        var source = CSharpEmitter.EmitType("Sample", "Ticker",
        [
            (new MethodContract
            {
                Name = ".ctor",
                DeclaringType = "Ticker",
                Namespace = "Sample",
                Access = "public",
                DeclaredReturnType = "System.Void",
                Parameters = [new ParameterContract { Name = "value", Type = PrimitiveKind.Int32, DeclaredType = "System.Int32" }]
            }, CSharpEmitter.Placeholder(MethodRecoveryStatus.Unsupported, ["body not recovered"]), true),
            (new MethodContract
            {
                Name = "Run",
                DeclaringType = "Ticker",
                Namespace = "Sample",
                Access = "private",
                IsVirtual = true,
                DeclaredReturnType = "System.Collections.IEnumerator",
                Parameters = []
            }, CSharpEmitter.Placeholder(MethodRecoveryStatus.Unsupported, ["Return type 'System.Collections.IEnumerator' is outside the lifter subset."]), true)
        ], "UnityEngine.MonoBehaviour");
        Assert.Contains("public class Ticker : UnityEngine.MonoBehaviour", source);
        Assert.Contains("public Ticker(int value) // metadata method name: .ctor", source);
        Assert.Contains("private System.Collections.IEnumerator Run()", source);
        Assert.DoesNotContain("private virtual", source);
        Assert.Contains("private method that is also virtual", source);
        Assert.DoesNotContain("int Run", source);
        Assert.DoesNotContain("_ctor", source);
    }

    [Fact]
    public void Nested_enum_is_declared_inside_its_type()
    {
        var catalog = new MetadataCatalog
        {
            Images =
            [
                new CatalogImage
                {
                    Name = "Assembly-CSharp.dll",
                    Types =
                    [
                        new CatalogType { Name = "BoostersController", Namespace = "Sample", MetadataIndex = 1 },
                        new CatalogType { Name = "BoosterTypes", Namespace = "", MetadataIndex = 2, IsEnum = true, IsValueType = true, DeclaringTypeIndex = 1 },
                        new CatalogType { Name = "Button", Namespace = "Sample", MetadataIndex = 3 }
                    ]
                }
            ]
        };
        var types = new List<EngineTypeInfo>
        {
            new() { MetadataIndex = 1, Image = "Assembly-CSharp.dll", Namespace = "Sample", Name = "BoostersController", BaseType = "UnityEngine.MonoBehaviour" },
            new()
            {
                MetadataIndex = 2, Image = "Assembly-CSharp.dll", Namespace = "", Name = "BoosterTypes", BaseType = "System.Enum", IsEnum = true, IsValueType = true,
                Fields =
                [
                    new EngineFieldInfo { Name = "value__", Type = "System.Int32", Access = "public", Offset = 0x10 },
                    new EngineFieldInfo { Name = "Speed", Type = "Sample.BoostersController+BoosterTypes", Access = "public", IsStatic = true, IsLiteral = true, ConstantValue = 0 },
                    new EngineFieldInfo { Name = "Shield", Type = "Sample.BoostersController+BoosterTypes", Access = "public", IsStatic = true, IsLiteral = true, ConstantValue = -1 }
                ]
            },
            new()
            {
                MetadataIndex = 3, Image = "Assembly-CSharp.dll", Namespace = "Sample", Name = "Button", BaseType = "UnityEngine.MonoBehaviour",
                Fields = [new EngineFieldInfo { Name = "kind", Type = "Sample.BoostersController+BoosterTypes", Access = "public", Offset = 0x18 }]
            }
        };
        var method = new MethodRecord
        {
            Assembly = "Assembly-CSharp.dll", Namespace = "Sample", DeclaringType = "Button", TypeMetadataIndex = 3,
            Name = "Use", Signature = "", ReturnTypeName = "System.Void",
            ParameterNames = ["type"], ParameterTypeNames = ["Sample.BoostersController+BoosterTypes"],
            Access = "public", Status = MethodRecoveryStatus.Unsupported,
            CSharp = CSharpEmitter.Placeholder(MethodRecoveryStatus.Unsupported, ["test"]), CSharpIsPlaceholder = true
        };
        var root = Path.Combine(Path.GetTempPath(), "il2cpp-nested-" + Guid.NewGuid().ToString("N"));
        OutputWriter.Write(root, Inventory(), catalog, [method], Report(), [], null, types);
        var controller = File.ReadAllText(Path.Combine(root, "csharp", "Assembly-CSharp.dll", "Sample", "BoostersController.cs"));
        var button = File.ReadAllText(Path.Combine(root, "csharp", "Assembly-CSharp.dll", "Sample", "Button.cs"));
        Assert.Contains("public enum BoosterTypes", controller);
        Assert.Contains("Shield = -1,", controller);
        Assert.Contains("public Sample.BoostersController.BoosterTypes kind;", button);
        Assert.Contains("public void Use(Sample.BoostersController.BoosterTypes type)", button);
        Assert.DoesNotContain("+", button.Split('\n').Where(l => !l.TrimStart().StartsWith("//")).Aggregate("", (a, b) => a + "\n" + b));
    }

    private static MethodRecord Record(string name, string returnType, string[] names, string[] types, bool isStatic, MethodRecoveryStatus status, string[] diagnostics) =>
        new()
        {
            Assembly = "Assembly-CSharp.dll",
            Namespace = "Sample",
            DeclaringType = "Ticker",
            Name = name,
            Signature = "unresolved Sample.Ticker::" + name + "()",
            ReturnTypeName = returnType,
            ParameterNames = names,
            ParameterTypeNames = types,
            IsStatic = isStatic,
            Status = status,
            Diagnostics = diagnostics,
            CSharp = CSharpEmitter.Placeholder(status, diagnostics),
            CSharpIsPlaceholder = true
        };

    private static InventoryResult Inventory() => new()
    {
        ToolName = "Il2CppRecover",
        ToolVersion = "0.3.0",
        InputPath = "input",
        InputSha256 = "abc",
        InputKind = "apk"
    };

    private static RecoveryReport Report() => new()
    {
        ToolName = "Il2CppRecover",
        ToolVersion = "0.3.0",
        RecoveryEngine = "Il2CppRecover",
        RecoveryEngineVersion = "0.3.0",
        PinnedCpp2IlCommit = "none",
        PinnedCpp2IlCore = "none",
        PinnedLibCpp2Il = "none",
        PinnedDisarm = "none",
        Counts = new RecoveryCounts { Denominator = "methods" },
        BehaviouralValidation = "not run",
        Limitations = []
    };
}

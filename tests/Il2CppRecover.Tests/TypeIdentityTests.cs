using Il2CppRecover.EngineAdapter;
using Il2CppRecover.Recovery;
using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

public class TypeIdentityTests
{
    private const ulong Base = 0x1000;

    [Fact]
    public void Same_full_name_keeps_a_layout_per_type_id()
    {
        var tables = TypeTables.Build(
        [
            Widget(10, "Game.dll", "score"),
            Widget(20, "Other.dll", "lives")
        ]);
        Assert.Equal(1, tables.SameNameGroups);
        Assert.Equal("score", tables.FieldsById[10][0].Name);
        Assert.Equal("lives", tables.FieldsById[20][0].Name);
        Assert.Equal(10, tables.FieldsById[10][0].OwnerTypeId);
        Assert.Equal(20, tables.FieldsById[20][0].OwnerTypeId);

        var score = ReadField(10, tables.FieldsById[10]);
        var lives = ReadField(20, tables.FieldsById[20]);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, score.Status);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, lives.Status);
        Assert.Contains("score", score.CSharp);
        Assert.DoesNotContain("lives", score.CSharp);
        Assert.Contains("lives", lives.CSharp);
        Assert.DoesNotContain("score", lives.CSharp);
    }

    [Fact]
    public void Field_access_on_a_same_named_parameter_uses_that_type_id()
    {
        var request = new RecoveryRequest
        {
            Code = Arm64Asm.Words(Arm64Asm.LdrW(0, 1, 0x10), Arm64Asm.Ret()),
            VirtualAddress = Base,
            Contract = new MethodContract
            {
                Name = "Read",
                Namespace = "Game",
                DeclaringType = "Reader",
                DeclaringTypeId = 1,
                IsStatic = false,
                ReturnType = PrimitiveKind.Int32,
                Parameters =
                [
                    new ParameterContract { Name = "widget", Type = PrimitiveKind.Reference, DeclaredType = "Game.Widget", DeclaredTypeId = 20 }
                ],
                Fields = [new FieldContract { Name = "score", Type = PrimitiveKind.Int32, InstanceOffset = 0x10, OwnerTypeId = 10 }]
            },
            TypeFieldsById = new Dictionary<int, IReadOnlyList<FieldContract>>
            {
                [10] = [new FieldContract { Name = "score", Type = PrimitiveKind.Int32, InstanceOffset = 0x10, OwnerTypeId = 10 }],
                [20] = [new FieldContract { Name = "lives", Type = PrimitiveKind.Int32, InstanceOffset = 0x10, OwnerTypeId = 20 }]
            },
            TypeFields = new Dictionary<string, IReadOnlyList<FieldContract>>
            {
                ["Game.Widget"] = [new FieldContract { Name = "score", Type = PrimitiveKind.Int32, InstanceOffset = 0x10 }]
            }
        };
        var result = Arm64Recoverer.Recover(request);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Contains("widget.lives", result.CSharp);
        Assert.DoesNotContain("score", result.CSharp);
    }

    [Fact]
    public void Several_module_types_are_kept_and_are_not_emitted_as_classes()
    {
        var tables = TypeTables.Build(
        [
            Module(0, "mscorlib.dll", "token"),
            Module(1605, "UnityEngine.UIElementsModule.dll", "slot")
        ]);
        Assert.Equal(2, tables.ByName["<Module>"].Count);
        Assert.Equal("token", tables.ById[0].Fields[0].Name);
        Assert.Equal("slot", tables.ById[1605].Fields[0].Name);

        var catalog = Catalog(
            ("mscorlib.dll", [Type("<Module>", "", 0)]),
            ("UnityEngine.UIElementsModule.dll", [Type("<Module>", "", 1605)]));
        var root = Temp();
        OutputWriter.Write(root, Inventory(), catalog, [], Report(), [], null, [Module(0, "mscorlib.dll", "token"), Module(1605, "UnityEngine.UIElementsModule.dll", "slot")]);
        var first = File.ReadAllText(Path.Combine(root, "csharp", "mscorlib.dll", "_", "_Module_0.cs"));
        var second = File.ReadAllText(Path.Combine(root, "csharp", "UnityEngine.UIElementsModule.dll", "_", "_Module_1605.cs"));
        Assert.Contains("static int token;", first);
        Assert.Contains("static int slot;", second);
        Assert.Contains("ModuleMembers_0", first);
        Assert.Contains("ModuleMembers_1605", second);
        Assert.Contains("is not declared as class <Module>", first);
        Assert.DoesNotContain("public class <Module>", first);
        Assert.DoesNotContain("public class <Module>", second);
    }

    [Fact]
    public void Nested_types_with_the_same_name_stay_distinct()
    {
        var catalog = Catalog(
            ("Game.dll",
            [
                Type("Outer", "Game", 1),
                Type("Inner", "", 2, 1),
                Type("Other", "Game", 3),
                Type("Inner", "", 4, 3),
                Type("Pair", "Game", 5),
                Type("Inner", "", 6, 5),
                Type("Inner", "", 7, 5)
            ]),
            ("Other.dll",
            [
                Type("Outer", "Game", 8),
                Type("Inner", "", 9, 8)
            ]));
        var root = Temp();
        OutputWriter.Write(root, Inventory(), catalog, [], Report(), [], null, []);
        var outer = File.ReadAllText(Path.Combine(root, "csharp", "Game.dll", "Game", "Outer.cs"));
        var other = File.ReadAllText(Path.Combine(root, "csharp", "Game.dll", "Game", "Other.cs"));
        var pair = File.ReadAllText(Path.Combine(root, "csharp", "Game.dll", "Game", "Pair.cs"));
        var foreign = File.ReadAllText(Path.Combine(root, "csharp", "Other.dll", "Game", "Outer.cs"));
        Assert.Contains("class Inner", outer);
        Assert.Contains("class Inner", other);
        Assert.Contains("class Inner", foreign);
        Assert.Contains("class Inner_6", pair);
        Assert.Contains("class Inner_7", pair);
        Assert.DoesNotContain("class Inner\n", pair);
    }

    [Fact]
    public void Generic_references_compare_by_definition_and_arguments()
    {
        var a = Def(1, "A");
        var b = Def(2, "B");
        var listA = Instance(3, "System.Collections.Generic.List`1<A>", a);
        var listB = Instance(3, "System.Collections.Generic.List`1<B>", b);
        var listAAgain = Instance(3, "System.Collections.Generic.List`1<A>", Def(1, "A"));
        Assert.False(listA.SameAs(listB));
        Assert.True(listA.SameAs(listAAgain));

        var nested = Instance(4, "Outer`1<A>+Inner", a);
        var otherNested = Instance(5, "Outer`1<A>+Inner", a);
        Assert.False(nested.SameAs(otherNested));

        var typeParameter = new TypeRef { Kind = TypeRefKind.GenericParameter, GenericParameterName = "T", GenericParameterIndex = 0, Display = "T" };
        var methodParameter = new TypeRef { Kind = TypeRefKind.GenericParameter, GenericParameterName = "T", GenericParameterIndex = 0, MethodGenericParameter = true, Display = "T" };
        Assert.True(typeParameter.SameAs(new TypeRef { Kind = TypeRefKind.GenericParameter, GenericParameterName = "T", GenericParameterIndex = 0, Display = "T" }));
        Assert.False(typeParameter.SameAs(methodParameter));
    }

    [Fact]
    public void Inherited_fields_walk_the_base_chain_and_generic_bases_stay_unresolved()
    {
        var origin = Field("origin", 0x10);
        var mid = Field("mid", 0x18);
        var leaf = Field("leaf", 0x20);
        var open = Field("items", 0x10);
        var tables = TypeTables.Build(
        [
            new EngineTypeInfo { MetadataIndex = 1, Image = "Game.dll", Namespace = "Game", Name = "A", DisplayName = "Game.A", Fields = [origin] },
            new EngineTypeInfo { MetadataIndex = 2, Image = "Game.dll", Namespace = "Game", Name = "B", DisplayName = "Game.B", BaseType = "Game.A", BaseTypeRef = Def(1, "Game.A"), Fields = [mid] },
            new EngineTypeInfo { MetadataIndex = 3, Image = "Game.dll", Namespace = "Game", Name = "C", DisplayName = "Game.C", BaseType = "Game.B", BaseTypeRef = Def(2, "Game.B"), Fields = [leaf] },
            new EngineTypeInfo { MetadataIndex = 4, Image = "mscorlib.dll", Namespace = "System.Collections.Generic", Name = "List`1", DisplayName = "System.Collections.Generic.List`1", Fields = [open] },
            new EngineTypeInfo
            {
                MetadataIndex = 5, Image = "Game.dll", Namespace = "Game", Name = "Holder", DisplayName = "Game.Holder",
                BaseType = "System.Collections.Generic.List`1<Game.A>",
                BaseTypeRef = Instance(4, "System.Collections.Generic.List`1<Game.A>", Def(1, "Game.A")),
                Fields = [Field("extra", 0x20)]
            }
        ]);
        var layout = tables.FieldsById[3];
        Assert.Equal(["origin", "mid", "leaf"], layout.Select(field => field.Name).ToArray());
        Assert.Equal([1, 2, 3], layout.Select(field => field.OwnerTypeId).ToArray());

        var read = ReadField(3, layout, "C", "Game");
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, read.Status);
        Assert.Contains("origin", read.CSharp);

        Assert.DoesNotContain(tables.FieldsById[5], field => field.Name == "items");
        Assert.Contains("unresolved", tables.LayoutNotes[5]);
        var missed = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = Arm64Asm.Words(Arm64Asm.LdrW(0, 0, 0x10), Arm64Asm.Ret()),
            VirtualAddress = Base,
            Contract = new MethodContract
            {
                Name = "Read", Namespace = "Game", DeclaringType = "Holder", DeclaringTypeId = 5,
                ReturnType = PrimitiveKind.Int32, Fields = tables.FieldsById[5]
            },
            LayoutNotes = tables.LayoutNotes
        });
        Assert.NotEqual(MethodRecoveryStatus.RecoveredUnvalidated, missed.Status);
        Assert.Contains("unresolved", string.Join(" ", missed.Diagnostics));
        Assert.Contains("0x10", string.Join(" ", missed.Diagnostics));
    }

    [Fact]
    public void Export_paths_and_cross_assembly_names_are_disambiguated()
    {
        var shared = "Shared.Marker";
        var left = new EngineTypeInfo { MetadataIndex = 10, Image = "Game.dll", Namespace = "Shared", Name = "Marker", DisplayName = shared, Fields = [Field("left", 0x10)] };
        var also = new EngineTypeInfo { MetadataIndex = 15, Image = "Game.dll", Namespace = "Shared", Name = "Marker", DisplayName = shared, Fields = [Field("also", 0x18)] };
        var right = new EngineTypeInfo { MetadataIndex = 11, Image = "Other.dll", Namespace = "Shared", Name = "Marker", DisplayName = shared };
        var generatedA = new EngineTypeInfo { MetadataIndex = 12, Image = "Game.dll", Namespace = "", Name = "<PrivateImplementationDetails>", DisplayName = "<PrivateImplementationDetails>", Fields = [Field("a", 0x10)] };
        var generatedB = new EngineTypeInfo { MetadataIndex = 13, Image = "Game.dll", Namespace = "", Name = "<PrivateImplementationDetails>", DisplayName = "<PrivateImplementationDetails>", Fields = [Field("b", 0x10)] };
        var user = new EngineTypeInfo
        {
            MetadataIndex = 14, Image = "Game.dll", Namespace = "Game", Name = "User", DisplayName = "Game.User",
            Fields =
            [
                new EngineFieldInfo { Name = "known", Type = shared, TypeReference = Def(11, shared), Access = "public", Offset = 0x10 },
                new EngineFieldInfo { Name = "unknown", Type = shared, Access = "public", Offset = 0x18 }
            ]
        };
        var catalog = Catalog(("Game.dll",
        [
            Type("Marker", "Shared", 10),
            Type("Marker", "Shared", 15),
            Type("<PrivateImplementationDetails>", "", 12),
            Type("<PrivateImplementationDetails>", "", 13),
            Type("User", "Game", 14)
        ]), ("Other.dll", [Type("Marker", "Shared", 11)]));
        var root = Temp();
        OutputWriter.Write(root, Inventory(), catalog, [], Report(), [], null, [left, also, right, generatedA, generatedB, user]);
        var markerA = File.ReadAllText(Path.Combine(root, "csharp", "Game.dll", "Shared", "Marker_10.cs"));
        var markerB = File.ReadAllText(Path.Combine(root, "csharp", "Game.dll", "Shared", "Marker_15.cs"));
        Assert.Contains("class Marker_10", markerA);
        Assert.Contains("int left;", markerA);
        Assert.Contains("class Marker_15", markerB);
        Assert.Contains("int also;", markerB);
        Assert.True(File.Exists(Path.Combine(root, "csharp", "Other.dll", "Shared", "Marker.cs")));
        Assert.False(File.Exists(Path.Combine(root, "csharp", "Game.dll", "Shared", "Marker.cs")));
        var firstGenerated = File.ReadAllText(Path.Combine(root, "csharp", "Game.dll", "_", "_PrivateImplementationDetails__12.cs"));
        var secondGenerated = File.ReadAllText(Path.Combine(root, "csharp", "Game.dll", "_", "_PrivateImplementationDetails__13.cs"));
        Assert.Contains("int a;", firstGenerated);
        Assert.Contains("int b;", secondGenerated);
        Assert.DoesNotContain("int b;", firstGenerated);
        var userSource = File.ReadAllText(Path.Combine(root, "csharp", "Game.dll", "Game", "User.cs"));
        Assert.Contains("extern alias Other;", userSource);
        Assert.Contains("Other::Shared.Marker known;", userSource);
        Assert.Contains("ambiguous across assemblies", userSource);
        Assert.Contains("Game.dll index 10", userSource);
        Assert.Contains("Other.dll index 11", userSource);
    }

    [Fact]
    public void Duplicate_type_id_names_both_definitions_and_same_names_do_not()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TypeTables.Build(
        [
            Widget(4, "Game.dll", "score"),
            new EngineTypeInfo { MetadataIndex = 4, Image = "Other.dll", Namespace = "Game", Name = "Player", DisplayName = "Game.Player" }
        ]));
        Assert.Contains("Game.Widget", ex.Message);
        Assert.Contains("Game.Player", ex.Message);
        Assert.Contains("metadata index 4", ex.Message);

        var tables = TypeTables.Build([Widget(4, "Game.dll", "score"), Widget(5, "Other.dll", "lives")]);
        Assert.Equal(1, tables.SameNameGroups);
    }

    [Fact]
    public void Open_generic_offsets_stay_unresolved_and_explicit_overlaps_are_not_chosen()
    {
        var tables = TypeTables.Build(
        [
            new EngineTypeInfo
            {
                MetadataIndex = 1, Image = "mscorlib.dll", Namespace = "System", Name = "ArraySegment`1+Enumerator",
                DisplayName = "System.ArraySegment`1+Enumerator", IsOpenGeneric = true, IsValueType = true,
                Fields = [Field("_array", 0), Field("_start", 0)]
            },
            new EngineTypeInfo
            {
                MetadataIndex = 2, Image = "mscorlib.dll", Namespace = "System", Name = "Object", DisplayName = "System.Object",
                Fields = [Field("sync", 0x10)]
            },
            new EngineTypeInfo
            {
                MetadataIndex = 3, Image = "mscorlib.dll", Namespace = "System", Name = "Lazy`1", DisplayName = "System.Lazy`1",
                IsOpenGeneric = true, BaseTypeRef = Def(2, "System.Object"),
                Fields = [Field("_state", 0), Field("_value", 0)]
            },
            new EngineTypeInfo
            {
                MetadataIndex = 4, Image = "mscorlib.dll", Namespace = "System", Name = "Variant", DisplayName = "System.Variant",
                IsExplicitLayout = true, IsValueType = true,
                Fields = [Field("vt", 0), Field("lVal", 0x8), Field("llVal", 0x8)]
            }
        ]);
        Assert.Empty(tables.FieldsById[1]);
        Assert.Contains("open generic", tables.LayoutNotes[1]);
        Assert.Equal("sync", Assert.Single(tables.FieldsById[3]).Name);
        Assert.Contains("open generic", tables.LayoutNotes[3]);
        Assert.Equal("vt", Assert.Single(tables.FieldsById[4]).Name);
        Assert.Contains("lVal", tables.LayoutNotes[4]);
        Assert.Contains("llVal", tables.LayoutNotes[4]);
        Assert.Contains("No one field was chosen", tables.LayoutNotes[4]);

        var missed = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = Arm64Asm.Words(Arm64Asm.LdrW(0, 0, 0x8), Arm64Asm.Ret()),
            VirtualAddress = Base,
            Contract = new MethodContract
            {
                Name = "Read", Namespace = "System", DeclaringType = "Variant", DeclaringTypeId = 4,
                ReturnType = PrimitiveKind.Int32, Fields = tables.FieldsById[4]
            },
            LayoutNotes = tables.LayoutNotes
        });
        Assert.NotEqual(MethodRecoveryStatus.RecoveredUnvalidated, missed.Status);
        Assert.Contains("No one field was chosen", string.Join(" ", missed.Diagnostics));
        Assert.Contains("lVal", string.Join(" ", missed.Diagnostics));
        Assert.Contains("llVal", string.Join(" ", missed.Diagnostics));
    }

    [Fact]
    public void Two_fields_at_one_offset_name_both_records()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TypeTables.Build(
        [
            new EngineTypeInfo
            {
                MetadataIndex = 1, Image = "Game.dll", Namespace = "Game", Name = "A", DisplayName = "Game.A",
                Fields = [Field("left", 0x10), Field("right", 0x10)]
            }
        ]));
        Assert.Contains("left", ex.Message);
        Assert.Contains("right", ex.Message);
        Assert.Contains("0x10", ex.Message);
    }

    [Fact]
    public void SnakeWarzOnline_smoke_is_opt_in()
    {
        if (Environment.GetEnvironmentVariable("IL2CPP_RECOVER_SMOKE") != "1")
            return;
        var root = RepoRoot();
        var apk = Path.Combine(root, "SnakeWarzOnline.apk");
        Assert.True(File.Exists(apk), apk);
        var output = Path.Combine(Path.GetTempPath(), "il2cpp-smoke-" + Guid.NewGuid().ToString("N"));
        var analysis = AnalysisPipeline.Analyze(apk, output, new AnalysisOptions { RequestedAbi = "arm64-v8a" }, null, CancellationToken.None);
        Assert.True(analysis.Report.Counts.SameNameTypeGroups > 0);
        Assert.True(analysis.Methods.Count > 0);
        Assert.True(File.Exists(Path.Combine(output, "type-identity.json")));
    }

    private static MethodBodyResult ReadField(int typeId, IReadOnlyList<FieldContract> fields, string name = "Widget", string ns = "Game") =>
        Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = Arm64Asm.Words(Arm64Asm.LdrW(0, 0, 0x10), Arm64Asm.Ret()),
            VirtualAddress = Base,
            Contract = new MethodContract
            {
                Name = "Read",
                Namespace = ns,
                DeclaringType = name,
                DeclaringTypeId = typeId,
                ReturnType = PrimitiveKind.Int32,
                Fields = fields
            }
        });

    private static EngineTypeInfo Widget(int id, string image, string field) => new()
    {
        MetadataIndex = id,
        Image = image,
        Namespace = "Game",
        Name = "Widget",
        DisplayName = "Game.Widget",
        Fields = [Field(field, 0x10)]
    };

    private static EngineTypeInfo Module(int id, string image, string field) => new()
    {
        MetadataIndex = id,
        Image = image,
        Namespace = "",
        Name = "<Module>",
        DisplayName = "<Module>",
        Fields = [new EngineFieldInfo { Name = field, Type = "System.Int32", Access = "public", IsStatic = true }]
    };

    private static EngineFieldInfo Field(string name, int offset) =>
        new() { Name = name, Type = "System.Int32", Access = "public", Offset = offset };

    private static TypeRef Def(int id, string display) =>
        new() { Kind = TypeRefKind.Definition, DefinitionIndex = id, Display = display };

    private static TypeRef Instance(int id, string display, params TypeRef[] arguments) =>
        new() { Kind = TypeRefKind.GenericInstance, DefinitionIndex = id, Display = display, Arguments = arguments };

    private static CatalogType Type(string name, string ns, int id, int? parent = null) =>
        new() { Name = name, Namespace = ns, MetadataIndex = id, DeclaringTypeIndex = parent };

    private static MetadataCatalog Catalog(params (string Name, CatalogType[] Types)[] images) => new()
    {
        Images = images.Select(image => new CatalogImage { Name = image.Name, Types = image.Types }).ToList()
    };

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "il2cpp-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Il2CppRecover.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root was not found from " + AppContext.BaseDirectory);
    }

    private static InventoryResult Inventory() => new()
    {
        ToolName = "Il2CppRecover",
        ToolVersion = "0.4.0",
        InputPath = "input",
        InputSha256 = "abc",
        InputKind = "apk"
    };

    private static RecoveryReport Report() => new()
    {
        ToolName = "Il2CppRecover",
        ToolVersion = "0.4.0",
        RecoveryEngine = "Il2CppRecover",
        RecoveryEngineVersion = "0.4.0",
        PinnedCpp2IlCommit = "none",
        PinnedCpp2IlCore = "none",
        PinnedLibCpp2Il = "none",
        PinnedDisarm = "none",
        Counts = new RecoveryCounts { Denominator = "methods" },
        BehaviouralValidation = "not run",
        Limitations = []
    };
}

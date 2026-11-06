namespace Il2CppRecover.Tests.Helpers;

/// <summary>Field offsets and signatures for the fixture, matching the C++ layout. Bodies are not supplied.</summary>
public static class FixtureContracts
{
    private static FieldContract F(string n, PrimitiveKind k, int off, string decl) =>
        new() { Name = n, Type = k, InstanceOffset = off, DeclaredType = decl };

    public static readonly IReadOnlyList<FieldContract> Fields =
    [
        F("count", PrimitiveKind.Int32, 0x18, "System.Int32"),
        F("speed", PrimitiveKind.Single, 0x1C, "System.Single"),
        F("flag", PrimitiveKind.Boolean, 0x20, "System.Boolean"),
        F("next", PrimitiveKind.Reference, 0x28, "Fixture"),
        F("total", PrimitiveKind.Int32, 0x30, "System.Int32")
    ];

    private static MethodContract M(string name, PrimitiveKind ret, params (string, PrimitiveKind)[] ps) => new()
    {
        Name = name,
        DeclaringType = "Fixture",
        ReturnType = ret,
        DeclaredReturnType = ret switch
        {
            PrimitiveKind.Void => "System.Void",
            PrimitiveKind.Int32 => "System.Int32",
            PrimitiveKind.Single => "System.Single",
            _ => null
        },
        Fields = Fields,
        Parameters = ps.Select(p => new ParameterContract
        {
            Name = p.Item1,
            Type = p.Item2,
            DeclaredType = p.Item2 == PrimitiveKind.Single ? "System.Single" : "System.Int32"
        }).ToList()
    };

    public static readonly IReadOnlyList<(string Symbol, MethodContract Contract)> Methods =
    [
        ("Fixture_SmoothFollow_m", M("SmoothFollow", PrimitiveKind.Void)),
        ("Fixture_LateUpdate_m", M("LateUpdate", PrimitiveKind.Void)),
        ("Fixture_Tick_m", M("Tick", PrimitiveKind.Void)),
        ("Fixture__ctor_m", M(".ctor", PrimitiveKind.Void)),
        ("Fixture_SumNext_m", M("SumNext", PrimitiveKind.Int32)),
        ("Fixture_Clamp_m", M("Clamp", PrimitiveKind.Void, ("v", PrimitiveKind.Int32))),
        ("Fixture_Scaled_m", M("Scaled", PrimitiveKind.Single, ("dt", PrimitiveKind.Single))),
        ("Fixture_Toggle_m", M("Toggle", PrimitiveKind.Void)),
        ("Fixture_Pick_m", M("Pick", PrimitiveKind.Int32, ("a", PrimitiveKind.Int32), ("b", PrimitiveKind.Int32))),
        ("Fixture_NextTotal_m", M("NextTotal", PrimitiveKind.Int32)),
        ("Fixture_CopyToNext_m", M("CopyToNext", PrimitiveKind.Void))
    ];

    public static RecoveryRequest Request(FixtureBinary fx, string symbol, MethodContract contract, IReadOnlyDictionary<ulong, RuntimeHelper>? helpers)
    {
        var targets = Methods.ToDictionary(m => fx.Address(m.Symbol), m => m.Contract);
        targets[fx.Address("MonoBehaviour__ctor_m")] = new MethodContract
        {
            Name = ".ctor",
            Namespace = "UnityEngine",
            DeclaringType = "MonoBehaviour"
        };
        var address = fx.Address(symbol);
        var entries = Methods.Select(m => fx.Address(m.Symbol))
            .Append(fx.Address("MonoBehaviour__ctor_m"))
            .Append(fx.Address("il2cpp_codegen_raise_null_reference_exception"));
        return new RecoveryRequest
        {
            Code = fx.NextEntrySlice(address),
            VirtualAddress = address,
            Contract = contract,
            Boundary = BoundaryEvidence.NextMethodAddressOnly,
            Space = CodeSpace.ForElf(fx.Elf, fx.Bytes, entries),
            CallTargets = targets,
            TypeFields = new Dictionary<string, IReadOnlyList<FieldContract>> { ["Fixture"] = Fields },
            RuntimeHelpers = helpers
        };
    }
}

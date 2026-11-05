using Disarm;
using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

public class Arm64RecoveryTests
{
    private const ulong Base = 0x1000;

    [Fact]
    public void Encodings_match_disarm_mnemonics()
    {
        AssertMnemonic(Arm64Asm.Ret(), Arm64Mnemonic.RET);
        AssertMnemonic(Arm64Asm.Movz(0, 42), Arm64Mnemonic.MOVZ);
        AssertMnemonic(Arm64Asm.AddReg(0, 0, 1), Arm64Mnemonic.ADD);
        AssertMnemonic(Arm64Asm.CmpReg(0, 1), Arm64Mnemonic.CMP);
        AssertMnemonic(Arm64Asm.BCond(Arm64Asm.Lt, 12), Arm64Mnemonic.B);
        AssertMnemonic(Arm64Asm.Mul(0, 0, 1), Arm64Mnemonic.MUL);
        AssertMnemonic(Arm64Asm.LdrW(0, 0, 0x10), Arm64Mnemonic.LDR);
        AssertMnemonic(Arm64Asm.StrW(1, 0, 0x10), Arm64Mnemonic.STR);
        AssertMnemonic(Arm64Asm.B(-16), Arm64Mnemonic.B);
    }

    [Fact]
    public void Constant_return_matches_compiled_behaviour()
    {
        var result = Lift(Arm64Asm.Words(Arm64Asm.Movz(0, 42), Arm64Asm.Ret()), Method("Return42", PrimitiveKind.Int32, true));
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.False(result.CSharpIsPlaceholder);
        Assert.Equal(42, Run(result, Method("Return42", PrimitiveKind.Int32, true)));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2, 3, 5)]
    [InlineData(-1, 1, 0)]
    [InlineData(int.MaxValue, 1, int.MinValue)]
    [InlineData(int.MinValue, -1, int.MaxValue)]
    public void Add_uses_unchecked_wraparound(int a, int b, int expected)
    {
        var contract = Binary("Add");
        var result = Lift(Arm64Asm.Words(Arm64Asm.AddReg(0, 0, 1), Arm64Asm.Ret()), contract);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Equal(expected, Run(result, contract, a, b));
    }

    [Fact]
    public void Subtract_multiply_and_bitwise_match()
    {
        Check(Arm64Asm.Words(Arm64Asm.SubReg(0, 0, 1), Arm64Asm.Ret()), "Sub", 8, 3, 5);
        Check(Arm64Asm.Words(Arm64Asm.Mul(0, 0, 1), Arm64Asm.Ret()), "Mul", 100000, 100000, unchecked(100000 * 100000));
        Check(Arm64Asm.Words(Arm64Asm.And(0, 0, 1), Arm64Asm.Ret()), "And", 0b1100, 0b1010, 0b1000);
        Check(Arm64Asm.Words(Arm64Asm.Orr(0, 0, 1), Arm64Asm.Ret()), "Orr", 0b1100, 0b1010, 0b1110);
        Check(Arm64Asm.Words(Arm64Asm.Eor(0, 0, 1), Arm64Asm.Ret()), "Eor", 0b1100, 0b1010, 0b0110);
    }

    [Theory]
    [InlineData(1, 2, 2)]
    [InlineData(3, 3, 3)]
    [InlineData(-1, -5, -1)]
    [InlineData(int.MinValue, int.MaxValue, int.MaxValue)]
    public void Signed_max_recovers_both_returns(int a, int b, int expected)
    {
        var code = Arm64Asm.Words(
            Arm64Asm.CmpReg(0, 1),
            Arm64Asm.BCond(Arm64Asm.Lt, 8),
            Arm64Asm.Ret(),
            Arm64Asm.MovReg(0, 1),
            Arm64Asm.Ret());
        var contract = Binary("Max");
        var result = Lift(code, contract);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Contains("<", result.CSharp);
        Assert.Equal(expected, Run(result, contract, a, b));
    }

    [Theory]
    [InlineData(1u, 2u, true)]
    [InlineData(2u, 1u, false)]
    [InlineData(0xFFFFFFFFu, 1u, false)]
    [InlineData(1u, 0xFFFFFFFFu, true)]
    public void Unsigned_compare_does_not_use_signed_less_than(uint a, uint b, bool expected)
    {
        var code = Arm64Asm.Words(
            Arm64Asm.CmpReg(0, 1),
            Arm64Asm.BCond(Arm64Asm.Cc, 12),
            Arm64Asm.Movz(0, 0),
            Arm64Asm.Ret(),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret());
        var contract = Method("Below", PrimitiveKind.Boolean, true, null, ("a", PrimitiveKind.UInt32), ("b", PrimitiveKind.UInt32));
        var result = Lift(code, contract);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Contains("(uint)", result.CSharp);
        Assert.Equal(expected, Run(result, contract, a, b));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 10)]
    [InlineData(10, 45)]
    [InlineData(-1, 0)]
    public void While_sum_matches_observable_accumulation(int n, int expected)
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Movz(1, 0),
            Arm64Asm.Movz(2, 0),
            Arm64Asm.CmpReg(2, 0),
            Arm64Asm.BCond(Arm64Asm.Ge, 16),
            Arm64Asm.AddReg(1, 1, 2),
            Arm64Asm.AddImm(2, 2, 1),
            Arm64Asm.B(-16),
            Arm64Asm.MovReg(0, 1),
            Arm64Asm.Ret());
        var contract = Method("SumTo", PrimitiveKind.Int32, true, null, ("n", PrimitiveKind.Int32));
        var result = Lift(code, contract);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Contains("while", result.CSharp);
        Assert.Equal(expected, Run(result, contract, n));
    }

    [Fact]
    public void Instance_field_get_and_set_round_trip()
    {
        var field = new FieldContract { Name = "score", Type = PrimitiveKind.Int32, InstanceOffset = 0x10 };
        var getter = Method("GetScore", PrimitiveKind.Int32, false, [field]);
        var setter = Method("SetScore", PrimitiveKind.Void, false, [field], ("value", PrimitiveKind.Int32));
        var got = Lift(Arm64Asm.Words(Arm64Asm.LdrW(0, 0, 0x10), Arm64Asm.Ret()), getter);
        var set = Lift(Arm64Asm.Words(Arm64Asm.StrW(1, 0, 0x10), Arm64Asm.Ret()), setter);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, got.Status);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, set.Status);
        var source = CSharpEmitter.EmitType("RecoveryFixtures", "FieldFixture", [(getter, got.CSharp!, false), (setter, set.CSharp!, false)]);
        var type = Compile(source, "RecoveryFixtures.FieldFixture");
        var instance = Activator.CreateInstance(type)!;
        type.GetMethod("SetScore")!.Invoke(instance, [41]);
        Assert.Equal(41, type.GetField("score")!.GetValue(instance));
        Assert.Equal(41, type.GetMethod("GetScore")!.Invoke(instance, null));
    }

    [Fact]
    public void Static_field_load_uses_the_address_calculation()
    {
        var contract = Method("GetStatic", PrimitiveKind.Int32, true,
        [
            new FieldContract { Name = "score", Type = PrimitiveKind.Int32, IsStatic = true, AbsoluteAddress = 0x2000 }
        ]);
        var result = Lift(Arm64Asm.Words(Arm64Asm.Adrp(8, 1, 0), Arm64Asm.LdrW(0, 8, 0), Arm64Asm.Ret()), contract);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        var source = CSharpEmitter.EmitType("RecoveryFixtures", "StaticFixture", [(contract, result.CSharp!, false)]);
        var type = Compile(source, "RecoveryFixtures.StaticFixture");
        type.GetField("score")!.SetValue(null, 9);
        Assert.Equal(9, type.GetMethod("GetStatic")!.Invoke(null, null));
    }

    [Fact]
    public void Direct_call_keeps_argument_order()
    {
        var callee = new MethodContract
        {
            Name = "Add",
            DeclaringType = "ArithmeticFixture",
            IsStatic = true,
            ReturnType = PrimitiveKind.Int32,
            Parameters = [new ParameterContract { Name = "a", Type = PrimitiveKind.Int32 }, new ParameterContract { Name = "b", Type = PrimitiveKind.Int32 }]
        };
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = Arm64Asm.Words(Arm64Asm.Bl(0x1000), Arm64Asm.Ret()),
            VirtualAddress = Base,
            Contract = Binary("CallAdd"),
            CallTargets = new Dictionary<ulong, MethodContract> { [0x2000] = callee }
        });
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Contains("Add(a, b)", result.CSharp);
    }

    [Fact]
    public void Unsupported_instruction_is_a_labelled_placeholder()
    {
        var result = Lift(Arm64Asm.Words(0x1AC20820, Arm64Asm.Ret()), Binary("Div"));
        Assert.NotEqual(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.True(result.CSharpIsPlaceholder);
        Assert.Contains("IL2CPP_RECOVER_UNRECOVERED", result.CSharp);
        Assert.DoesNotContain("return 0;", result.CSharp);
    }

    [Fact]
    public void Next_method_boundary_is_not_promoted_to_recovered()
    {
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = Arm64Asm.Words(Arm64Asm.Movz(0, 7), Arm64Asm.Ret()),
            VirtualAddress = Base,
            Contract = Method("Constant", PrimitiveKind.Int32, true),
            Boundary = BoundaryEvidence.NextMethodAddressOnly
        });
        Assert.Equal(MethodRecoveryStatus.PartiallyRecovered, result.Status);
        Assert.True(result.CSharpIsPlaceholder);
        Assert.Contains("7", result.CandidateCSharp);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    public void In_slice_tbz_is_a_bit_test_not_an_external_branch(int value, int expected)
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Tbz(0, 0, 12),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret(),
            Arm64Asm.Movz(0, 2),
            Arm64Asm.Ret());
        var contract = Method("LowBitClear", PrimitiveKind.Int32, true, null, ("value", PrimitiveKind.Int32));
        var result = Lift(code, contract);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.False(result.CSharpIsPlaceholder);
        Assert.Contains("& 1", result.CSharp);
        Assert.DoesNotContain("outside the slice", string.Join(" ", result.Diagnostics));
        Assert.Equal(expected, Run(result, contract, value));
    }

    [Fact]
    public void Tbnz_keeps_the_set_bit_path()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Tbnz(0, 1, 12),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret(),
            Arm64Asm.Movz(0, 2),
            Arm64Asm.Ret());
        var contract = Method("Bit1Set", PrimitiveKind.Int32, true, null, ("value", PrimitiveKind.Int32));
        var result = Lift(code, contract);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Contains("& 2", result.CSharp);
        Assert.Equal(1, Run(result, contract, 0));
        Assert.Equal(2, Run(result, contract, 2));
    }

    [Fact]
    public void Tbz_target_outside_the_slice_stays_unrecovered()
    {
        var code = Arm64Asm.Words(Arm64Asm.Tbz(0, 0, 64), Arm64Asm.Movz(0, 1), Arm64Asm.Ret());
        var result = Lift(code, Method("Leaves", PrimitiveKind.Int32, true, null, ("value", PrimitiveKind.Int32)));
        Assert.Equal(MethodRecoveryStatus.Unsupported, result.Status);
        Assert.True(result.CSharpIsPlaceholder);
        Assert.Contains("outside the slice", string.Join(" ", result.Diagnostics));
        Assert.DoesNotContain("return 0;", result.CSharp);
        Assert.DoesNotContain("return 1;", result.CSharp);
    }

    [Fact]
    public void Unknown_memory_access_is_not_invented()
    {
        var result = Lift(Arm64Asm.Words(Arm64Asm.LdrW(0, 0, 0x20), Arm64Asm.Ret()), Method("GetScore", PrimitiveKind.Int32, false));
        Assert.NotEqual(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Contains("No pointer was invented", string.Join(" ", result.Diagnostics));
    }

    private static void Check(byte[] code, string name, int a, int b, int expected)
    {
        var contract = Binary(name);
        var result = Lift(code, contract);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Equal(expected, Run(result, contract, a, b));
    }

    private static MethodBodyResult Lift(byte[] code, MethodContract contract) =>
        Arm64Recoverer.Recover(new RecoveryRequest { Code = code, VirtualAddress = Base, Contract = contract });

    private static MethodContract Binary(string name) =>
        Method(name, PrimitiveKind.Int32, true, null, ("a", PrimitiveKind.Int32), ("b", PrimitiveKind.Int32));

    private static MethodContract Method(
        string name,
        PrimitiveKind ret,
        bool isStatic,
        FieldContract[]? fields = null,
        params (string Name, PrimitiveKind Type)[] parameters) =>
        new()
        {
            Name = name,
            Namespace = "RecoveryFixtures",
            DeclaringType = "ArithmeticFixture",
            IsStatic = isStatic,
            ReturnType = ret,
            Parameters = parameters.Select(p => new ParameterContract { Name = p.Name, Type = p.Type }).ToList(),
            Fields = fields ?? []
        };

    private static object? Run(MethodBodyResult result, MethodContract contract, params object?[] args)
    {
        var source = CSharpEmitter.EmitType(contract.Namespace, contract.DeclaringType, [(contract, result.CSharp!, false)]);
        return RoslynRunner.Invoke(source, contract.Namespace + "." + contract.DeclaringType, contract.Name, null, args);
    }

    private static System.Type Compile(string source, string typeName)
    {
        var references = new List<Microsoft.CodeAnalysis.MetadataReference>();
        foreach (var path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Path.GetFileName(path) is "System.Runtime.dll" or "System.Private.CoreLib.dll")
                references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        }
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "Compiled",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics) + Environment.NewLine + source);
        return System.Reflection.Assembly.Load(pe.ToArray()).GetType(typeName)!;
    }

    private static void AssertMnemonic(uint word, Arm64Mnemonic mnemonic)
    {
        var insn = Disassembler.Disassemble(BitConverter.GetBytes(word), Base, new Disassembler.Options(true, true, false)).First();
        Assert.Equal(mnemonic, insn.Mnemonic);
    }
}

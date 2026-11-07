using System.IO.Compression;
using Il2CppRecover.Analysis;
using Il2CppRecover.EngineAdapter;
using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

/// <summary>
/// Byte sequences taken from SnakeWarzOnline CameraController (Unity 2022.3.4f1, arm64).
/// The words were checked with llvm-mc 18. The numeric checks compare the lifted C# with
/// an independent IEEE implementation. Unicorn is not installed in this environment.
/// </summary>
public class CameraControllerPatternTests
{
    private const ulong Base = 0x1000;

    [Fact]
    public void Guard_after_a_simd_move_is_elided()
    {
        var code = Arm64Asm.Words(
            0x1E2703E0,
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrB(8, 9, 0x4E7),
            0x4EA01C08,
            Arm64Asm.Tbnz(8, 0, 24),
            Arm64Asm.Adrp(0, 2, 0),
            Arm64Asm.LdrX(0, 0, 0xA00),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.StrB(8, 9, 0x4E7),
            Arm64Asm.Movz(0, 7),
            Arm64Asm.Ret());
        var result = Recover(code, Int("AfterSimdGuard"));
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains(result.Diagnostics, line => line.Contains("elided IL2CPP lazy runtime-metadata init", StringComparison.Ordinal));
        Assert.Equal(7, Run(result, Int("AfterSimdGuard")));
    }

    [Fact]
    public void Guard_whose_flag_register_is_overwritten_is_not_elided()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrB(8, 9, 0x4E7),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.Tbnz(8, 0, 24),
            Arm64Asm.Adrp(0, 2, 0),
            Arm64Asm.LdrX(0, 0, 0xA00),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.StrB(8, 9, 0x4E7),
            Arm64Asm.Movz(0, 7),
            Arm64Asm.Ret());
        var result = Recover(code, Int("OverwrittenFlag"));
        Assert.DoesNotContain(result.Diagnostics, line => line.Contains("elided IL2CPP lazy runtime-metadata init", StringComparison.Ordinal));
        Assert.NotEqual(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
    }

    [Fact]
    public void Whole_byte_cbnz_guard_is_elided()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrB(8, 9, 0x555),
            Cbnz(8, 24),
            Arm64Asm.Adrp(0, 2, 0),
            Arm64Asm.LdrX(0, 0, 0xA00),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.StrB(8, 9, 0x555),
            Arm64Asm.Movz(0, 3),
            Arm64Asm.Ret());
        var result = Recover(code, Int("CbnzGuard"));
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains(result.Diagnostics, line => line.Contains("whole-byte test", StringComparison.Ordinal));
        Assert.Equal(3, Run(result, Int("CbnzGuard")));
    }

    [Fact]
    public void Guard_that_reuses_a_register_held_entry_is_elided()
    {
        // RegisterNewPlayer 0xD6E674: the usage pointer is already in x20.
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrB(8, 9, 0x558),
            Cbnz(8, 24),
            Arm64Asm.MovReg64(0, 10),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.LdrX(0, 10, 0),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.StrB(8, 9, 0x558),
            Arm64Asm.Movz(0, 3),
            Arm64Asm.Ret());
        var result = Recover(code, Int("HeldEntryGuard"));
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains(result.Diagnostics, line => line.Contains("elided IL2CPP lazy runtime-metadata init", StringComparison.Ordinal));
        Assert.Equal(3, Run(result, Int("HeldEntryGuard")));
    }

    [Theory]
    [InlineData(0x1E2703E0, 0)]
    [InlineData(0x1E270100, 0x3E99999A)]
    public void Fmov_copies_bits_and_wzr_is_positive_zero(uint fmov, int sourceBits)
    {
        uint[] words = sourceBits == 0
            ? [fmov, Arm64Asm.Ret()]
            : [0x52933348, 0x72A7D328, fmov, Arm64Asm.Ret()];
        var contract = Method("Bits", PrimitiveKind.Single, true);
        var result = Recover(Arm64Asm.Words(words), contract);
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        var value = (float)Run(result, contract)!;
        Assert.Equal(sourceBits, BitConverter.SingleToInt32Bits(value));
        if (sourceBits == 0)
            Assert.Contains("0f", result.CSharp);
    }

    [Fact]
    public void Gpr_constant_store_into_a_float_field_is_the_bit_pattern()
    {
        var field = new FieldContract { Name = "smoothAmount", Type = PrimitiveKind.Single, InstanceOffset = 0x48 };
        var contract = Method("Init", PrimitiveKind.Void, false, [field]);
        var result = Recover(Arm64Asm.Words(0x52933348, 0x72A7D328, Arm64Asm.StrW(8, 0, 0x48), Arm64Asm.Ret()), contract);
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("0.3f", result.CSharp);
        var source = CSharpEmitter.EmitType(contract.Namespace, contract.DeclaringType, [(contract, result.CSharp!, false)]);
        var type = Compile(source, contract.Namespace + "." + contract.DeclaringType);
        var instance = Activator.CreateInstance(type)!;
        type.GetMethod("Init")!.Invoke(instance, null);
        var stored = (float)type.GetField("smoothAmount")!.GetValue(instance)!;
        Assert.Equal(unchecked((int)0x3E99999A), BitConverter.SingleToInt32Bits(stored));
    }

    [Fact]
    public void Zero_register_store_into_a_float_field_is_positive_zero()
    {
        var field = new FieldContract { Name = "additionalFov", Type = PrimitiveKind.Single, InstanceOffset = 0x3C };
        var contract = Method("Clear", PrimitiveKind.Void, false, [field]);
        var result = Recover(Arm64Asm.Words(Arm64Asm.StrW(31, 0, 0x3C), Arm64Asm.Ret()), contract);
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("0f", result.CSharp);
        var source = CSharpEmitter.EmitType(contract.Namespace, contract.DeclaringType, [(contract, result.CSharp!, false)]);
        var type = Compile(source, contract.Namespace + "." + contract.DeclaringType);
        var instance = Activator.CreateInstance(type)!;
        type.GetField("additionalFov")!.SetValue(instance, 4f);
        type.GetMethod("Clear")!.Invoke(instance, null);
        var stored = (float)type.GetField("additionalFov")!.GetValue(instance)!;
        Assert.Equal(0, BitConverter.SingleToInt32Bits(stored));
    }

    [Fact]
    public void Float_pair_on_an_object_is_two_fields()
    {
        var fields = new[]
        {
            new FieldContract { Name = "low", Type = PrimitiveKind.Single, InstanceOffset = 0x20 },
            new FieldContract { Name = "high", Type = PrimitiveKind.Single, InstanceOffset = 0x24 }
        };
        var store = Method("Store", PrimitiveKind.Void, false, fields, ("low", PrimitiveKind.Single), ("high", PrimitiveKind.Single));
        var load = Method("LoadLow", PrimitiveKind.Single, false, fields);
        var stored = Recover(Arm64Asm.Words(0x2D040400, Arm64Asm.Ret()), store);
        var loaded = Recover(Arm64Asm.Words(0x2D440400, Arm64Asm.Ret()), load);
        Assert.True(stored.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", stored.Diagnostics));
        Assert.Contains("low", stored.CSharp);
        Assert.Contains("high", stored.CSharp);
        _ = loaded;
    }

    [Theory]
    [InlineData(0x3F800000, 0x40000000)]
    [InlineData(unchecked((int)0x80000000), 0)]
    [InlineData(0, unchecked((int)0x80000000))]
    [InlineData(0x7FC00000, 0x3F800000)]
    [InlineData(0x3F800000, 0x7FC00000)]
    [InlineData(0x7F800000, unchecked((int)0xBF800000))]
    public void Fmin_then_fcsel_matches_ieee_min_and_pl(int aBits, int bBits)
    {
        var contract = Method("ClampMin", PrimitiveKind.Single, true, null, ("a", PrimitiveKind.Single), ("b", PrimitiveKind.Single));
        var result = Recover(Arm64Asm.Words(0x1E215800, 0x1E2703E2, 0x1E202008, 0x1E225C00, Arm64Asm.Ret()), contract);
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.DoesNotContain("MathF.Min", result.CSharp);
        var a = BitConverter.Int32BitsToSingle(aBits);
        var b = BitConverter.Int32BitsToSingle(bBits);
        var value = (float)Run(result, contract, a, b)!;
        Assert.Equal(BitConverter.SingleToInt32Bits(ExpectedClamp(a, b)), BitConverter.SingleToInt32Bits(value));
    }

    [Fact]
    public void Fsqrt_matches_mathf_sqrt()
    {
        var contract = Float("Root");
        var result = Recover(Arm64Asm.Words(0x1E21C000, Arm64Asm.Ret()), contract);
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("MathF.Sqrt", result.CSharp);
        var value = (float)Run(result, contract, 4f)!;
        Assert.Equal(2f, value);
        value = (float)Run(result, contract, 2f)!;
        Assert.Equal(BitConverter.SingleToInt32Bits(MathF.Sqrt(2f)), BitConverter.SingleToInt32Bits(value));
    }

    [Fact]
    public void Class_init_check_becomes_run_class_constructor()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrX(9, 9, 0),
            Arm64Asm.LdrX(0, 9, 0),
            Arm64Asm.LdrW(8, 0, 0xE0),
            Cbnz(8, 8),
            Arm64Asm.Bl(0x4000),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Int("NeedsInit"),
            MetadataVersion = 29,
            ResolveMetadataSlot = address => address == 0x2000 ? new MetadataSlot(MetadataSlotKind.Type, "Game.Camera", false, 7) : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("RunClassConstructor(typeof(Game.Camera).TypeHandle)", result.CSharp);
    }

    [Fact]
    public void Class_init_after_a_metadata_guard_is_not_poisoned_by_the_cold_path()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(10, 1, 0),
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrB(8, 10, 0x4E4),
            Arm64Asm.LdrX(9, 9, 0),
            Arm64Asm.Tbnz(8, 0, 24),
            Arm64Asm.Adrp(0, 2, 0),
            Arm64Asm.LdrX(0, 0, 0xA00),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.StrB(8, 10, 0x4E4),
            Arm64Asm.LdrX(0, 9, 0),
            Arm64Asm.LdrW(8, 0, 0xE0),
            Cbnz(8, 8),
            Arm64Asm.Bl(0x4000),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Int("AfterGuard"),
            MetadataVersion = 29,
            ResolveMetadataSlot = address => address == 0x2000 ? new MetadataSlot(MetadataSlotKind.Type, "Game.Camera", false, 7) : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("RunClassConstructor(typeof(Game.Camera).TypeHandle)", result.CSharp);
    }

    [Fact]
    public void Helper_that_reaches_cxa_throw_throws_the_object_in_x0()
    {
        var code = Arm64Asm.Words(Arm64Asm.Bl(0x1000));
        var helper = Arm64Asm.Bl(0x1000);
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = new MethodContract
            {
                Name = "Reset",
                Namespace = "Game",
                DeclaringType = "NotSupportedException",
                ReturnType = PrimitiveKind.Void
            },
            Space = new CodeSpace(
                address => address == 0x2000 ? helper : address == Base ? BitConverter.ToUInt32(code, 0) : null,
                address => address == Base || address == 0x2000,
                [Base],
                new Dictionary<ulong, string> { [0x2000] = "reaches __cxa_throw" }),
            CodeSymbols = new Dictionary<ulong, string> { [0x3000] = "__cxa_throw" }
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("throw this;", result.CSharp);
        Assert.Contains("__cxa_throw", string.Join(" ", result.Diagnostics));
    }

    [Fact]
    public void Parameterless_helper_that_names_NullReferenceException_throws_that_type()
    {
        // The shape of 0xCA5EF0's callee: adrp/add of "System" and "NullReferenceException", then a branch that reaches __cxa_throw.
        var code = Arm64Asm.Words(Arm64Asm.Bl(0x1100));
        uint[] helper =
        [
            Arm64Asm.Adrp(1, 1, 0),
            Arm64Asm.AddImm(1, 1, 0),
            Arm64Asm.Adrp(2, 1, 0),
            Arm64Asm.AddImm(2, 2, 16),
            Arm64Asm.B(0x1EF0)
        ];
        var raise = Arm64Asm.Bl(0x1000);
        var text = new byte[64];
        System.Text.Encoding.ASCII.GetBytes("System").CopyTo(text, 0);
        System.Text.Encoding.ASCII.GetBytes("NullReferenceException").CopyTo(text, 16);
        uint? ReadWord(ulong address) => address switch
        {
            Base => BitConverter.ToUInt32(code, 0),
            >= 0x2100 and < 0x2114 => helper[(int)((address - 0x2100) / 4)],
            0x4000 => raise,
            _ => null
        };
        byte? ReadByte(ulong address) => address >= 0x3000 && address < 0x3000 + (ulong)text.Length ? text[(int)(address - 0x3000)] : null;
        bool Executable(ulong address) => address == Base || address is >= 0x2100 and < 0x2114 || address == 0x4000;
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Method("Guard", PrimitiveKind.Void, true),
            Space = new CodeSpace(ReadWord, Executable, [Base], new Dictionary<ulong, string> { [0x2100] = "noreturn" }, ReadByte),
            CodeSymbols = new Dictionary<ulong, string> { [0x5000] = "__cxa_throw" }
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("throw new System.NullReferenceException()", result.CSharp);
    }

    [Fact]
    public void Class_init_reloads_the_klass_after_the_call_is_elided()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrX(9, 9, 0),
            Arm64Asm.LdrX(0, 9, 0),
            Arm64Asm.LdrW(8, 0, 0xE0),
            Cbnz(8, 12),
            Arm64Asm.Bl(0x4000),
            Arm64Asm.LdrX(0, 9, 0),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Int("ReloadKlass"),
            MetadataVersion = 29,
            ResolveMetadataSlot = address => address == 0x2000 ? new MetadataSlot(MetadataSlotKind.Type, "Game.Camera", false, 7) : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("RunClassConstructor(typeof(Game.Camera).TypeHandle)", result.CSharp);
    }

    [Fact]
    public void Class_init_moves_the_klass_into_x0_then_reloads_it()
    {
        // UpdateFOV 0xD6EB0C: the klass is in x8, so the cold path is mov x0, x8; bl; ldr x8, [entry].
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrX(9, 9, 0),
            Arm64Asm.LdrX(8, 9, 0),
            Arm64Asm.LdrW(10, 8, 0xE0),
            Cbnz(10, 16),
            Arm64Asm.MovReg64(0, 8),
            Arm64Asm.Bl(0x4000),
            Arm64Asm.LdrX(8, 9, 0),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Int("MoveKlass"),
            MetadataVersion = 29,
            ResolveMetadataSlot = address => address == 0x2000 ? new MetadataSlot(MetadataSlotKind.Type, "Game.Camera", false, 7) : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("RunClassConstructor(typeof(Game.Camera).TypeHandle)", result.CSharp);
    }

    [Fact]
    public void Class_init_before_a_static_store_of_the_same_type_is_left_to_csharp()
    {
        var field = new FieldContract
        {
            Name = "currentUnzoomValue",
            Type = PrimitiveKind.Single,
            IsStatic = true,
            StaticOffset = 8,
            OwnerType = "Game.Camera",
            OwnerTypeId = 7
        };
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrX(9, 9, 0),
            Arm64Asm.LdrX(0, 9, 0),
            Arm64Asm.LdrW(8, 0, 0xE0),
            Cbnz(8, 8),
            Arm64Asm.Bl(0x4000),
            Arm64Asm.LdrX(8, 0, 0xB8),
            Arm64Asm.StrW(31, 8, 8),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Method("ResetZoom", PrimitiveKind.Int32, true, [field]),
            MetadataVersion = 29,
            TypeFieldsById = new Dictionary<int, IReadOnlyList<FieldContract>> { [7] = [field] },
            ResolveMetadataSlot = address => address == 0x2000 ? new MetadataSlot(MetadataSlotKind.Type, "Game.Camera", false, 7) : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("Game.Camera.currentUnzoomValue = 0f", result.CSharp);
        Assert.DoesNotContain("RunClassConstructor", result.CSharp);
    }

    [Fact]
    public void Class_init_before_a_static_null_check_is_left_to_csharp()
    {
        var field = new FieldContract
        {
            Name = "instance",
            Type = PrimitiveKind.Reference,
            IsStatic = true,
            StaticOffset = 0,
            OwnerType = "Game.Camera",
            OwnerTypeId = 7,
            DeclaredType = "Game.Camera"
        };
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrX(9, 9, 0),
            Arm64Asm.LdrX(0, 9, 0),
            Arm64Asm.LdrW(8, 0, 0xE0),
            Cbnz(8, 8),
            Arm64Asm.Bl(0x4000),
            Arm64Asm.LdrX(8, 0, 0xB8),
            Arm64Asm.LdrX(8, 8, 0),
            Arm64Asm.Cbz64(8, 12),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret(),
            Arm64Asm.Movz(0, 2),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Method("Use", PrimitiveKind.Int32, true, [field]),
            MetadataVersion = 29,
            TypeFieldsById = new Dictionary<int, IReadOnlyList<FieldContract>> { [7] = [field] },
            ResolveMetadataSlot = address => address == 0x2000 ? new MetadataSlot(MetadataSlotKind.Type, "Game.Camera", false, 7) : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("Game.Camera.instance", result.CSharp);
        Assert.DoesNotContain("RunClassConstructor", result.CSharp);
    }

    [Fact]
    public void String_literal_slot_is_a_csharp_string()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(8, 1, 0),
            Arm64Asm.LdrX(8, 8, 0),
            Arm64Asm.LdrX(0, 8, 0),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Method("Name", PrimitiveKind.Reference, true),
            ResolveMetadataSlot = address => address == 0x2000
                ? new MetadataSlot(MetadataSlotKind.StringLiteral, "PlayerHead", false, LiteralText: "PlayerHead")
                : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("\"PlayerHead\"", result.CSharp);
    }

    [Fact]
    public void Iterator_MoveNext_maps_to_IEnumerator_through_the_vtable_range()
    {
        var apk = Path.Combine(FixtureBinary.RepoRoot(), "SnakeWarzOnline.apk");
        if (!File.Exists(apk))
            return;
        using var zip = ZipFile.OpenRead(apk);
        var loaded = Cpp2IlSession.TryLoad(Entry(zip, "lib/arm64-v8a/libil2cpp.so"), Entry(zip, "assets/bin/Data/Managed/Metadata/global-metadata.dat"), "2022.3.4f1");
        Assert.True(loaded.Loaded, loaded.Diagnostic);
        var move = loaded.Methods.Single(method => method.DeclaringType == "<FadeoutCo>d__22" && method.Name == "MoveNext");
        Assert.Equal("System.Collections.IEnumerator.MoveNext", move.ExplicitInterfaceName);
    }

    private static byte[] Entry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [Fact]
    public void Allocation_thunk_stays_explicit_and_object_ctor_is_not_new()
    {
        const ulong thunk = 0x8000;
        var code = Arm64Asm.Words(
            0xA9BF7BFD,
            0x910003FD,
            Arm64Asm.Adrp(8, 1, 0),
            Arm64Asm.LdrX(8, 8, 0),
            Arm64Asm.LdrX(0, 8, 0),
            Arm64Asm.Bl((int)(thunk - (Base + 20))),
            0xA8C17BFD,
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Method("Make", PrimitiveKind.Reference, true),
            Space = new CodeSpace(
                address =>
                {
                    if (address == thunk)
                        return 0x14000000u;
                    var index = (long)address - (long)Base;
                    return index >= 0 && index + 4 <= code.Length && index % 4 == 0 ? BitConverter.ToUInt32(code, (int)index) : null;
                },
                address => address == thunk || address >= Base && address < Base + (ulong)code.Length,
                [Base]),
            ResolveMetadataSlot = address => address == 0x2000 ? new MetadataSlot(MetadataSlotKind.Type, "Game.Fade", false, 4) : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("GetUninitializedObject(typeof(Game.Fade))", result.CSharp);
        Assert.DoesNotContain("new Game.Fade(", result.CSharp);
    }

    [Fact]
    public void Iterator_export_has_interfaces_and_no_private_virtual()
    {
        var declaration = new CSharpEmitter.TypeDeclaration
        {
            Name = "FadeoutCo",
            TypeId = 22,
            Interfaces = ["System.Collections.IEnumerator", "System.Collections.Generic.IEnumerator<System.Object>", "System.IDisposable"],
            Methods =
            [
                (new MethodContract { Name = "System.IDisposable.Dispose", DeclaringType = "FadeoutCo", Access = "private", IsVirtual = true, ReturnType = PrimitiveKind.Void }, "return;", false),
                (new MethodContract { Name = "System.Collections.IEnumerator.MoveNext", DeclaringType = "FadeoutCo", Access = "private", IsVirtual = true, ReturnType = PrimitiveKind.Boolean }, "return false;", false),
                (new MethodContract { Name = "Hidden", DeclaringType = "FadeoutCo", Access = "private", IsVirtual = true, ReturnType = PrimitiveKind.Void }, "return;", false)
            ]
        };
        var text = CSharpEmitter.EmitFile("Game", declaration);
        Assert.Contains("System.Collections.IEnumerator", text);
        Assert.Contains("System.Collections.Generic.IEnumerator<object>", text);
        Assert.Contains("System.IDisposable", text);
        Assert.Contains("void System.IDisposable.Dispose()", text);
        Assert.Contains("bool System.Collections.IEnumerator.MoveNext()", text);
        Assert.DoesNotContain("private virtual", text);
        Assert.Contains("private void Hidden()", text);
        Assert.Contains("private method that is also virtual", text);
    }

    [Fact]
    public void Feature_complete_requires_recovered_gameplay_callees()
    {
        var callee = Record("Assembly-CSharp.dll", "Calculate", "0x2000", MethodRecoveryStatus.Unsupported, null, true, []);
        var late = Record("Assembly-CSharp.dll", "LateUpdate", "0x1000", MethodRecoveryStatus.RecoveredUnvalidated, "Calculate();", false,
            [new NativeInstruction { Address = "0x1000", Bytes = "00000000", Text = "0x1000 BL 0x2000" }]);
        var report = RecoveryReportBuilder.Build([late, callee]);
        Assert.False(late.FeatureComplete);
        Assert.Equal(0, report.Breakdown.First(stage => stage.Scope.StartsWith("Gameplay", StringComparison.Ordinal)).FeatureComplete);

        var unity = Record("UnityEngine.CoreModule.dll", "get_deltaTime", "0x3000", MethodRecoveryStatus.Unsupported, null, true, []);
        var leaf = Record("Assembly-CSharp.dll", "Fadeout", "0x1100", MethodRecoveryStatus.RecoveredUnvalidated, "Time.deltaTime;", false,
            [new NativeInstruction { Address = "0x1100", Bytes = "00000000", Text = "0x1100 BL 0x3000" }]);
        var external = RecoveryReportBuilder.Build([leaf, unity]);
        Assert.True(leaf.FeatureComplete);
        Assert.Equal(1, external.Breakdown.First(stage => stage.Scope.StartsWith("Gameplay", StringComparison.Ordinal)).FeatureComplete);

        callee = Record("Assembly-CSharp.dll", "Calculate", "0x2000", MethodRecoveryStatus.RecoveredUnvalidated, "return;", false, []);
        late = Record("Assembly-CSharp.dll", "LateUpdate", "0x1000", MethodRecoveryStatus.RecoveredUnvalidated, "Calculate();", false,
            [new NativeInstruction { Address = "0x1000", Bytes = "00000000", Text = "0x1000 BL 0x2000" }]);
        var done = RecoveryReportBuilder.Build([late, callee]);
        Assert.True(late.FeatureComplete);
        Assert.Equal(2, done.Breakdown.First(stage => stage.Scope.StartsWith("Gameplay", StringComparison.Ordinal)).FeatureComplete);
    }

    private static float ExpectedClamp(float a, float b)
    {
        float min;
        if (float.IsNaN(a) || float.IsNaN(b))
            min = float.NaN;
        else if (a < b || (a == b && float.IsNegative(a)))
            min = a;
        else
            min = b;
        return !(min < 0f) ? min : 0f;
    }

    private static uint Cbnz(int rt, int byteDelta) => 0x35000000u | ((((uint)(byteDelta >> 2)) & 0x7FFFFu) << 5) | (uint)rt;

    private static MethodBodyResult Recover(byte[] code, MethodContract contract) =>
        Arm64Recoverer.Recover(new RecoveryRequest { Code = code, VirtualAddress = Base, Contract = contract });

    private static MethodContract Int(string name) => Method(name, PrimitiveKind.Int32, true);

    private static MethodContract Float(string name) =>
        Method(name, PrimitiveKind.Single, true, null, ("value", PrimitiveKind.Single));

    private static MethodContract Method(string name, PrimitiveKind ret, bool isStatic, FieldContract[]? fields = null, params (string Name, PrimitiveKind Type)[] parameters) =>
        new()
        {
            Name = name,
            Namespace = "RecoveryFixtures",
            DeclaringType = "CameraFixture",
            IsStatic = isStatic,
            ReturnType = ret,
            Parameters = parameters.Select(p => new ParameterContract { Name = p.Name, Type = p.Type }).ToList(),
            Fields = fields ?? []
        };

    private static MethodRecord Record(string assembly, string name, string address, MethodRecoveryStatus status, string? csharp, bool placeholder, NativeInstruction[] instructions) =>
        new()
        {
            Assembly = assembly,
            Namespace = "Game",
            DeclaringType = "Camera",
            Name = name,
            Signature = "void Game.Camera::" + name + "()",
            NativeVirtualAddress = address,
            Status = status,
            CSharp = csharp,
            CSharpIsPlaceholder = placeholder,
            Instructions = instructions
        };

    private static object? Run(MethodBodyResult result, MethodContract contract, params object?[] args)
    {
        var source = CSharpEmitter.EmitType(contract.Namespace, contract.DeclaringType, [(contract, result.CSharp!, false)]);
        return RoslynRunner.Invoke(source, contract.Namespace + "." + contract.DeclaringType, contract.Name, null, args);
    }

    private static System.Type Compile(string source, string typeName)
    {
        var references = new List<Microsoft.CodeAnalysis.MetadataReference>();
        var assembly = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        foreach (var file in new[] { "System.Runtime.dll", "System.Private.CoreLib.dll" })
        {
            var path = Path.Combine(assembly, file);
            if (File.Exists(path))
                references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        }
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("pattern", [tree], references, new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        var emitted = compilation.Emit(pe);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var loaded = System.Reflection.Assembly.Load(pe.ToArray());
        return loaded.GetType(typeName)!;
    }
}

using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

public class MetadataInitTests
{
    private const ulong Base = 0x1000;

    [Fact]
    public void Lazy_metadata_guard_is_elided_and_the_method_remains()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrB(8, 9, 0x492),
            Arm64Asm.Tbnz(8, 0, 24),
            Arm64Asm.Adrp(0, 2, 0),
            Arm64Asm.LdrX(0, 0, 0xA00),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.StrB(8, 9, 0x492),
            Arm64Asm.Movz(0, 7),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(Request(code));
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("7", result.CSharp);
        Assert.Contains(result.Diagnostics, d => d.Contains("lazy runtime-metadata init", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("global data", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_slot_inits_in_one_guard_are_elided()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrB(8, 9, 0x498),
            Arm64Asm.Tbnz(8, 0, 36),
            Arm64Asm.Adrp(0, 2, 0),
            Arm64Asm.LdrX(0, 0, 0xA00),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.Adrp(0, 3, 0),
            Arm64Asm.LdrX(0, 0, 0xA08),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.StrB(8, 9, 0x498),
            Arm64Asm.Movz(0, 4),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(Request(code));
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("4", result.CSharp);
        Assert.Contains(result.Diagnostics, d => d.Contains("lazy runtime-metadata init", StringComparison.Ordinal));
    }

    [Fact]
    public void Float_arithmetic_does_not_kill_a_null_checked_pointer()
    {
        var fn = new DiscoveredFunction { Entry = 0x6000 };
        Add(fn, 0x6000, Arm64Asm.Cbz64(0, 12));
        Add(fn, 0x6004, 0x1E211800);
        Add(fn, 0x6008, Arm64Asm.B(0x10000));
        Add(fn, 0x600C, Arm64Asm.Bl(0x8000 - 0x600C));
        fn.NoReturnCalls[0x600C] = "after a float divide";
        Assert.True(RuntimeHelperInference.IsNullCheck(fn, 0x600C));
    }

    [Fact]
    public void Guard_with_an_extra_cold_instruction_is_not_elided()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(9, 1, 0),
            Arm64Asm.LdrB(8, 9, 0x492),
            Arm64Asm.Tbnz(8, 0, 28),
            Arm64Asm.Adrp(0, 2, 0),
            Arm64Asm.LdrX(0, 0, 0xA00),
            Arm64Asm.Bl(0x2000),
            Arm64Asm.Nop(),
            Arm64Asm.Movz(8, 1),
            Arm64Asm.StrB(8, 9, 0x492),
            Arm64Asm.Movz(0, 7),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(Request(code));
        Assert.Contains(result.Diagnostics, d => d.Contains("global data", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("lazy runtime-metadata init", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolved_usage_slot_is_named_and_not_invented_as_an_object()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(8, 1, 0),
            Arm64Asm.LdrX(8, 8, 0),
            Arm64Asm.LdrX(1, 8, 0),
            Arm64Asm.Movz(0, 7),
            Arm64Asm.Ret());
        var slot = new MetadataSlot(MetadataSlotKind.Method, "Game.Hero::Attack", false);
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = code,
            VirtualAddress = Base,
            Contract = Contract(),
            ResolveMetadataSlot = address => address == 0x2000 ? slot : null
        });
        Assert.True(result.Status == MethodRecoveryStatus.RecoveredUnvalidated, string.Join(" | ", result.Diagnostics));
        Assert.Contains("7", result.CSharp);
        Assert.Contains(result.Diagnostics, d => d.Contains("Game.Hero::Attack", StringComparison.Ordinal));
    }

    [Fact]
    public void Unresolved_global_load_still_names_the_address()
    {
        var code = Arm64Asm.Words(
            Arm64Asm.Adrp(8, 1, 0),
            Arm64Asm.LdrX(8, 8, 0),
            Arm64Asm.Movz(0, 1),
            Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(Request(code));
        Assert.Contains(result.Diagnostics, d => d.Contains("global data at 0x2000", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_null_checks_may_share_one_outlined_call()
    {
        var fn = new DiscoveredFunction { Entry = 0x3000 };
        Add(fn, 0x3000, Arm64Asm.Cbz64(20, 24));
        Add(fn, 0x3004, Arm64Asm.MovReg64(0, 20));
        Add(fn, 0x3008, Arm64Asm.B(0x10000));
        Add(fn, 0x300C, Arm64Asm.Cbz64(20, 12));
        Add(fn, 0x3010, Arm64Asm.LdrX(0, 20, 0x10));
        Add(fn, 0x3014, Arm64Asm.Ret());
        Add(fn, 0x3018, Arm64Asm.Bl(0x8000 - 0x3018));
        fn.NoReturnCalls[0x3018] = "shared cold call";
        Assert.True(RuntimeHelperInference.IsNullCheck(fn, 0x3018));

        Add(fn, 0x300C, Arm64Asm.B(12));
        Assert.False(RuntimeHelperInference.IsNullCheck(fn, 0x3018));
    }

    [Fact]
    public void A_much_smaller_second_helper_does_not_hide_the_dominant_one()
    {
        var dominant = Enumerable.Range(0, 30).Select(i => NullCheck((ulong)(0x2000 + i * 0x100), 0x8000));
        var other = Enumerable.Range(0, 3).Select(i => NullCheck((ulong)(0x8000 + i * 0x100), 0x9000));
        var helpers = RuntimeHelperInference.InferNullReferenceRaiser(dominant.Concat(other));
        Assert.Equal(0x8000UL, Assert.Single(helpers.Keys));
        Assert.Empty(RuntimeHelperInference.InferNullReferenceRaiser(
            Enumerable.Range(0, 3).Select(i => NullCheck((ulong)(0x2000 + i * 0x100), 0x8000))
                .Concat(Enumerable.Range(0, 3).Select(i => NullCheck((ulong)(0x4000 + i * 0x100), 0x9000)))));
    }

    [Fact]
    public void Tail_call_null_check_identifies_one_helper_and_a_minority_does_not()
    {
        var matching = Enumerable.Range(0, 3).Select(i => NullCheck((ulong)(0x2000 + i * 0x100), 0x8000)).ToList();
        var helpers = RuntimeHelperInference.InferNullReferenceRaiser(matching);
        Assert.True(helpers.ContainsKey(0x8000), "three tail-call null checks");

        var mixed = matching.Take(2).Append(BareCall(0x5000, 0x8000)).ToList();
        Assert.Empty(RuntimeHelperInference.InferNullReferenceRaiser(mixed));
    }

    [Fact]
    public void Identified_helper_is_not_applied_to_a_call_without_the_null_check_shape()
    {
        var bare = BareCall(0x5000, 0x8000);
        var request = new RecoveryRequest
        {
            Code = [],
            VirtualAddress = bare.Entry,
            Contract = Contract(),
            RuntimeHelpers = new Dictionary<ulong, RuntimeHelper>
            {
                [0x8000] = new(RuntimeHelperKind.RaiseNullReferenceException, "test helper")
            }
        };
        var outcome = StructuredLifter.Lift(request, bare);
        var text = string.Join(" ", outcome.Diagnostics);
        Assert.DoesNotContain("NullReferenceException", text);
        Assert.Contains("noreturn", text, StringComparison.OrdinalIgnoreCase);
    }

    private static DiscoveredFunction NullCheck(ulong entry, ulong helper)
    {
        var bl = entry + 12;
        var fn = new DiscoveredFunction { Entry = entry };
        Add(fn, entry, Arm64Asm.Cbz64(20, 12));
        Add(fn, entry + 4, Arm64Asm.MovReg64(0, 20));
        Add(fn, entry + 8, Arm64Asm.B(0x10000));
        Add(fn, bl, Arm64Asm.Bl((int)(helper - bl)));
        fn.NoReturnCalls[bl] = "test noreturn";
        return fn;
    }

    private static DiscoveredFunction BareCall(ulong entry, ulong helper)
    {
        var fn = new DiscoveredFunction { Entry = entry };
        Add(fn, entry, Arm64Asm.Bl((int)(helper - entry)));
        fn.NoReturnCalls[entry] = "test noreturn";
        return fn;
    }

    private static void Add(DiscoveredFunction fn, ulong address, uint word) =>
        fn.Instructions[address] = A64Decoder.Decode(word, address);

    private static RecoveryRequest Request(byte[] code) =>
        new() { Code = code, VirtualAddress = Base, Contract = Contract() };

    private static MethodContract Contract() => new()
    {
        Name = "AfterGuard",
        Namespace = "RecoveryFixtures",
        DeclaringType = "GuardFixture",
        IsStatic = true,
        ReturnType = PrimitiveKind.Int32
    };
}

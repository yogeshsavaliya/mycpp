using Disarm;
using Il2CppRecover.Analysis;
using Il2CppRecover.Recovery;
using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

public class DuplicateAddressTests
{
    [Fact]
    public void Disarm_reports_address_zero_for_undecodable_words()
    {
        var code = TwoZerosThenRet();
        var disarm = Disassembler.Disassemble(code, 0x7B1690, new Disassembler.Options(true, true, false)).ToList();
        Assert.Equal(3, disarm.Count);
        Assert.Equal(0ul, disarm[0].Address);
        Assert.Equal(0ul, disarm[1].Address);
        Assert.Equal(Arm64Mnemonic.INVALID, disarm[0].Mnemonic);
        Assert.Equal(Arm64Mnemonic.INVALID, disarm[1].Mnemonic);
        Assert.Contains("INVALID", disarm[0].ToString(), StringComparison.Ordinal);
        Assert.Equal(0x7B1698ul, disarm[2].Address);
    }

    [Fact]
    public void Two_undecodable_words_keep_per_position_addresses_and_do_not_throw()
    {
        var code = TwoZerosThenRet();
        var listing = Arm64Recoverer.Disassemble(code, 0x7B1690);
        Assert.Equal(["0x7B1690", "0x7B1694", "0x7B1698"], listing.Select(i => i.Address).ToArray());
        Assert.Contains("INVALID", listing[0].Text, StringComparison.Ordinal);
        Assert.Contains("INVALID", listing[1].Text, StringComparison.Ordinal);
        Assert.Equal("00000000", listing[0].Bytes);
        Assert.Equal("00000000", listing[1].Bytes);

        var result = Arm64Recoverer.Recover(Request(code, 0x7B1690, "GetText"));
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("Key: 0x0", StringComparison.Ordinal));
    }

    [Fact]
    public void Relative_offset_zero_is_a_real_instruction()
    {
        var code = Arm64Asm.Words(Arm64Asm.Ret());
        var atZero = Arm64Recoverer.Disassemble(code, 0);
        Assert.Equal("0x0", Assert.Single(atZero).Address);
        var recovered = Arm64Recoverer.Recover(Request(code, 0, "AtZero"));
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, recovered.Status);
        Assert.Contains("return", recovered.CSharp, StringComparison.Ordinal);

        var self = Arm64Asm.Words(Arm64Asm.Cbz(0, 0), Arm64Asm.Ret());
        var branched = Arm64Recoverer.Recover(Request(self, 0x1000, "Self"));
        Assert.NotNull(branched);
        Assert.Equal("0x1000", branched.Instructions[0].Address);
    }

    [Fact]
    public void Conflicting_listing_records_name_both_records()
    {
        var ex = Assert.Throws<AddressInvariantException>(() => SliceAddressing.RequireDistinct(
        [
            new NativeInstruction { Address = "0x1000", Bytes = "aaaaaaaa", Text = "first word" },
            new NativeInstruction { Address = "0x1000", Bytes = "bbbbbbbb", Text = "second word" }
        ]));
        Assert.Contains("aaaaaaaa", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bbbbbbbb", ex.Message, StringComparison.Ordinal);
        Assert.Contains("first word", ex.Message, StringComparison.Ordinal);
        Assert.Contains("second word", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddressInvariantException", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Methods_without_a_native_pointer_are_not_executable_entries()
    {
        var entries = CallTargetIndex.ExecutableEntries([0, 0, 0x1000, 0x1000, 0x2000]);
        Assert.Equal([0x1000ul, 0x2000ul], entries);
        var indexed = CallTargetIndex.IndexByMetadata(
            new[] { new Row(1, "only") },
            row => row.Index,
            row => row.Text);
        Assert.Equal("only", indexed[1].Text);
        var duplicate = Assert.Throws<InvalidOperationException>(() => CallTargetIndex.IndexByMetadata(
            new[] { new Row(7, "first record va=0x10"), new Row(7, "second record va=0x20") },
            row => row.Index,
            row => row.Text));
        Assert.Contains("first record va=0x10", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("second record va=0x20", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_native_address_keeps_both_identities_and_stays_ambiguous()
    {
        var result = CallTargetIndex.Build(
        [
            (0x1000, Contract("Enemy", "Update", virtualMethod: false)),
            (0x2000, Contract("String", "Equals", virtualMethod: true)),
            (0x2000, Contract("Object", "Equals", virtualMethod: true))
        ]);
        Assert.Equal(1, result.SharedAddresses);
        Assert.Equal("Update", result.Resolved[0x1000].Name);
        Assert.Null(result.Resolved[0x1000].UnsupportedReason);
        Assert.Equal(2, result.All[0x2000].Count);
        Assert.Contains("String::Equals", result.Resolved[0x2000].UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("Object::Equals", result.Resolved[0x2000].UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_discovery_is_idempotent()
    {
        var code = Arm64Asm.Words(Arm64Asm.Cbz(0, 8), Arm64Asm.Nop(), Arm64Asm.Ret());
        var space = CodeSpace.ForSlice(code, 0x1000);
        var first = FunctionDiscovery.Discover(space, 0x1000);
        var second = FunctionDiscovery.Discover(space, 0x1000);
        Assert.Equal(first.Instructions.Keys, second.Instructions.Keys);
        foreach (var address in first.Instructions.Keys)
        {
            Assert.Equal(first.Instructions[address].Kind, second.Instructions[address].Kind);
            Assert.Equal(first.Instructions[address].Target, second.Instructions[address].Target);
        }
        Assert.Equal(first.Issues, second.Issues);
        Assert.Equal(first.Edges, second.Edges);
        Assert.Contains(first.Edges, edge => edge.From == 0x1000 && edge.To == 0x1008);
        Assert.Contains(first.Edges, edge => edge.From == 0x1000 && edge.To == 0x1004);

        var cache = new DiscoveryCache(4);
        var cached = cache.GetOrDiscover(space, 0x1000);
        Assert.Same(cached, cache.GetOrDiscover(space, 0x1000));
        Assert.Equal(cached.Instructions.Keys, first.Instructions.Keys);
    }

    [Fact]
    public void One_method_exception_does_not_change_the_next_method()
    {
        var failed = MethodIsolation.TryRecover(() => throw new InvalidOperationException("boom"), out var stack);
        Assert.Equal(MethodRecoveryStatus.Failed, failed.Status);
        Assert.Contains("InternalError: InvalidOperationException: boom", failed.Diagnostics);
        Assert.Contains("InvalidOperationException", stack, StringComparison.Ordinal);
        Assert.Contains("boom", stack, StringComparison.Ordinal);
        Assert.True(failed.CSharpIsPlaceholder);

        var code = Arm64Asm.Words(Arm64Asm.Ret());
        var next = MethodIsolation.TryRecover(() => Arm64Recoverer.Recover(Request(code, 0x1000, "Next")), out var nextStack);
        Assert.Null(nextStack);
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, next.Status);
        Assert.DoesNotContain(next.Diagnostics, d => d.StartsWith("InternalError:", StringComparison.Ordinal));
    }

    [Fact]
    public void Direct_call_on_this_to_a_base_virtual_is_base_qualified()
    {
        var code = Arm64Asm.Words(0xA9BF7BFD, 0x910003FD, Arm64Asm.Bl(0x1000 - 8), 0xA8C17BFD, Arm64Asm.Ret());
        var targets = new Dictionary<ulong, MethodContract>
        {
            [0x2000] = Contract("Object", "Finalize", virtualMethod: true, ns: "System")
        };
        var result = Arm64Recoverer.Recover(Request(code, 0x1000, "Finalize", "Enemy", "Game", targets));
        Assert.True(result.CSharp?.Contains("base.Finalize()", StringComparison.Ordinal) == true, result.Status + " " + result.CSharp + " | " + string.Join(" || ", result.Diagnostics));
        Assert.DoesNotContain("this.Finalize()", result.CSharp, StringComparison.Ordinal);

        targets[0x2000] = Contract("Enemy", "Finalize", virtualMethod: true, ns: "Game");
        var same = Arm64Recoverer.Recover(Request(code, 0x1000, "Finalize", "Enemy", "Game", targets));
        Assert.Contains("this.Finalize()", same.CSharp, StringComparison.Ordinal);
        Assert.DoesNotContain("base.Finalize()", same.CSharp, StringComparison.Ordinal);

        targets[0x2000] = Contract("Object", "MemberwiseClone", virtualMethod: false, ns: "System");
        var plain = Arm64Recoverer.Recover(Request(code, 0x1000, "Clone", "Enemy", "Game", targets));
        Assert.Contains("this.MemberwiseClone()", plain.CSharp, StringComparison.Ordinal);
        Assert.DoesNotContain("base.MemberwiseClone()", plain.CSharp, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_type_metadata_index_names_both_records()
    {
        var catalog = new MetadataCatalog
        {
            Images =
            [
                new CatalogImage
                {
                    Name = "Game.dll",
                    Types =
                    [
                        new CatalogType { Name = "Enemy", Namespace = "Game", MetadataIndex = 4 },
                        new CatalogType { Name = "Player", Namespace = "Game", MetadataIndex = 4 }
                    ]
                }
            ]
        };
        var root = Path.Combine(Path.GetTempPath(), "il2cpp-dup-type-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<InvalidOperationException>(() => OutputWriter.Write(
            root,
            new InventoryResult { ToolName = "Il2CppRecover", ToolVersion = "0.4.0", InputPath = "input", InputSha256 = "abc", InputKind = "apk" },
            catalog,
            [],
            new RecoveryReport
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
            },
            [],
            null));
        Assert.Contains("Game.Enemy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Game.Player", ex.Message, StringComparison.Ordinal);
        Assert.Contains("metadata index 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Block_at_address_zero_is_not_treated_as_a_missing_postdominator()
    {
        var code = Arm64Asm.Words(Arm64Asm.Ret());
        var result = Arm64Recoverer.Recover(Request(code, 0, "Exit"));
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.Equal("0x0", Assert.Single(result.Instructions).Address);
    }

    private static byte[] TwoZerosThenRet()
    {
        var code = new byte[12];
        var ret = Arm64Asm.Ret();
        code[8] = (byte)ret;
        code[9] = (byte)(ret >> 8);
        code[10] = (byte)(ret >> 16);
        code[11] = (byte)(ret >> 24);
        return code;
    }

    private static RecoveryRequest Request(byte[] code, ulong address, string name, string type = "Locale", string ns = "System", IReadOnlyDictionary<ulong, MethodContract>? targets = null) => new()
    {
        Code = code,
        VirtualAddress = address,
        Contract = Contract(type, name, virtualMethod: true, ns),
        CallTargets = targets,
        Boundary = BoundaryEvidence.CallerSuppliedSlice
    };

    private static MethodContract Contract(string type, string name, bool virtualMethod, string ns = "") => new()
    {
        Name = name,
        DeclaringType = type,
        Namespace = ns,
        IsVirtual = virtualMethod,
        ReturnType = PrimitiveKind.Void
    };

    private sealed record Row(int Index, string Text);
}

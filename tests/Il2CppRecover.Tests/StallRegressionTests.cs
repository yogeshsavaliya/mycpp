using System.Diagnostics;
using Il2CppRecover.Analysis;
using Il2CppRecover.Recovery;
using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

public class StallRegressionTests
{
    [Fact(Timeout = 15000)]
    public async Task Large_linear_cfg_finishes_without_quadratic_memory()
    {
        await Task.Yield();
        var code = Chain(8000);
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var started = Stopwatch.GetTimestamp();
        var outcome = Lift(code);
        var elapsed = Stopwatch.GetElapsedTime(started);
        GC.Collect();
        var retained = GC.GetTotalMemory(true) - before;
        Assert.True(elapsed < TimeSpan.FromSeconds(3), "linear CFG took " + elapsed);
        Assert.True(retained < 80L * 1024 * 1024, "linear CFG retained " + retained);
        Assert.False(outcome.HasLoop);
        Assert.NotNull(outcome.Statements);
    }

    [Fact(Timeout = 15000)]
    public async Task Highly_branching_cfg_finishes()
    {
        await Task.Yield();
        var started = Stopwatch.GetTimestamp();
        var outcome = Lift(Diamonds(300));
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(elapsed < TimeSpan.FromSeconds(3), "branching CFG took " + elapsed);
        Assert.False(outcome.HasLoop);
        Assert.NotNull(outcome.Statements);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.StartsWith("AnalysisBudgetExceeded", StringComparison.Ordinal));
    }

    [Fact(Timeout = 5000)]
    public async Task Cyclic_branch_and_thunk_chain_terminate()
    {
        await Task.Yield();
        var loop = Lift(Arm64Asm.Words(Arm64Asm.Cbz(0, 0), Arm64Asm.Ret()));
        Assert.True(loop.HasLoop);

        var space = Space(Arm64Asm.Words(Arm64Asm.B(4), Arm64Asm.B(-4)), 0x2000, 0x2000, 0x2004);
        var cycle = FunctionDiscovery.ResolveThunk(space, 0x2000, _ => false);
        Assert.False(cycle.Resolved);
        Assert.Contains("cycles", cycle.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancellation_during_helper_analysis_propagates()
    {
        var words = new List<uint>();
        for (var i = 0; i < 400; i++)
            words.Add(Arm64Asm.Nop());
        words.Add(Arm64Asm.Ret());
        var code = Arm64Asm.Words(words.ToArray());
        var cts = new CancellationTokenSource();
        var reads = 0;
        var space = new CodeSpace(
            address =>
            {
                if (++reads == 80)
                    cts.Cancel();
                var offset = (int)(address - 0x1000);
                return offset >= 0 && offset + 4 <= code.Length ? BitConverter.ToUInt32(code, offset) : null;
            },
            address => address >= 0x1000 && address < 0x1000 + (ulong)code.Length,
            [0x1000]);
        Assert.Throws<OperationCanceledException>(() => RuntimeHelperScan.Scan(space, [0x1000], cts.Token));
    }

    [Fact]
    public void Cancellation_during_method_recovery_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = Request(Arm64Asm.Words(Arm64Asm.Ret()), cancellationToken: cts.Token);
        Assert.Throws<OperationCanceledException>(() => Arm64Recoverer.Recover(request));
    }

    [Fact(Timeout = 5000)]
    public async Task Analysis_budget_is_not_counted_as_recovered()
    {
        await Task.Yield();
        var request = Request(Chain(32), new AnalysisBudget { MaxBlocks = 8, MethodDeadline = TimeSpan.FromSeconds(5) });
        var result = Arm64Recoverer.Recover(request);
        Assert.Equal(MethodRecoveryStatus.AnalysisBudgetExceeded, result.Status);
        Assert.NotEqual(MethodRecoveryStatus.RecoveredUnvalidated, result.Status);
        Assert.True(result.CSharpIsPlaceholder);
        Assert.Contains(result.Diagnostics, d => d.StartsWith("AnalysisBudgetExceeded", StringComparison.Ordinal));
    }

    [Fact(Timeout = 5000)]
    public async Task Method_deadline_is_recorded_as_timed_out()
    {
        await Task.Yield();
        var request = Request(Chain(64), new AnalysisBudget { MethodDeadline = TimeSpan.Zero });
        var result = Arm64Recoverer.Recover(request);
        Assert.Equal(MethodRecoveryStatus.TimedOut, result.Status);
        Assert.True(result.CSharpIsPlaceholder);
        Assert.Contains(result.Diagnostics, d => d.StartsWith("TimedOut", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_cache_drops_results_when_noreturn_facts_change()
    {
        var code = Arm64Asm.Words(Arm64Asm.Bl(8), Arm64Asm.Ret(), Arm64Asm.Ret());
        var space = Space(code, 0x1000, 0x1000, 0x1008);
        var cache = new DiscoveryCache(4);
        var first = cache.GetOrDiscover(space, 0x1000);
        Assert.Empty(first.NoReturnCalls);
        Assert.True(space.AddNoReturn(0x1008, "test noreturn"));
        var stale = cache.GetOrDiscover(space, 0x1000);
        Assert.Same(first, stale);
        cache.Invalidate();
        var fresh = cache.GetOrDiscover(space, 0x1000);
        Assert.NotSame(first, fresh);
        Assert.NotEmpty(fresh.NoReturnCalls);
        Assert.Equal(2, cache.Misses);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void Gameplay_first_leaves_framework_bodies_unanalyzed()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2cpp-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var apk = Path.Combine(root, "game.apk");
        var metadata = MetadataFixture.Version31("mscorlib.dll", "mscorlib", "System", "String", "Equals", ["value"], false);
        ApkBuilder.Write(apk, [
            ("lib/arm64-v8a/libil2cpp.so", ElfBuilder.SharedObject(183), CompressionLevel.NoCompression),
            ("assets/bin/Data/Managed/Metadata/global-metadata.dat", metadata, CompressionLevel.NoCompression),
            ("assets/bin/Data/globalgamemanagers", ApkBuilder.GlobalGameManagers("2022.3.62f3"), CompressionLevel.NoCompression)
        ]);
        var output = Path.Combine(root, "out");
        var result = AnalysisPipeline.Analyze(apk, output, new AnalysisOptions { GameplayFirst = true, SuppliedUnityVersion = "2022.3.62f3" }, null, CancellationToken.None);
        var method = Assert.Single(result.Methods);
        Assert.Equal(MethodRecoveryStatus.NotAnalyzed, method.Status);
        Assert.Contains(method.Diagnostics, d => d.Contains("Gameplay-first", StringComparison.Ordinal));
        Assert.Equal(0, result.Report.Counts.FullyRecoveredUniqueBodies);
        Assert.Equal(1, result.Report.Counts.NotAnalyzed);
        Assert.Equal("gameplay-first", result.Report.BodyAnalysisScope);
        Assert.Equal("gameplay-first: framework body not analyzed", Assert.Single(result.Report.Breakdown.Single(b => b.Scope.StartsWith("All", StringComparison.Ordinal)).UnrecoveredByReason.Keys));
        var trace = File.ReadAllText(Path.Combine(output, "analysis-trace.jsonl"));
        Assert.Contains(result.Inventory.InputSha256, trace, StringComparison.Ordinal);
        Assert.Contains("pid", trace, StringComparison.Ordinal);
    }

    private static StructuredLifter.Outcome Lift(byte[] code)
    {
        var space = CodeSpace.ForSlice(code, 0x1000);
        var fn = FunctionDiscovery.Discover(space, 0x1000);
        return StructuredLifter.Lift(Request(code), fn);
    }

    private static RecoveryRequest Request(byte[] code, AnalysisBudget? budget = null, CancellationToken cancellationToken = default) => new()
    {
        Code = code,
        VirtualAddress = 0x1000,
        Space = CodeSpace.ForSlice(code, 0x1000),
        Budget = budget ?? AnalysisBudget.Default,
        CancellationToken = cancellationToken,
        Contract = new MethodContract
        {
            Name = "M",
            DeclaringType = "T",
            IsStatic = true,
            ReturnType = PrimitiveKind.Void,
            Parameters = [new ParameterContract { Name = "a", Type = PrimitiveKind.Int32 }]
        }
    };

    private static CodeSpace Space(byte[] code, ulong address, params ulong[] entries) => new(
        candidate =>
        {
            var offset = (int)(candidate - address);
            return offset >= 0 && offset + 4 <= code.Length ? BitConverter.ToUInt32(code, offset) : null;
        },
        candidate => candidate >= address && candidate < address + (ulong)code.Length,
        entries);

    private static byte[] Chain(int blocks)
    {
        var words = new List<uint>();
        for (var i = 0; i < blocks; i++)
        {
            words.Add(Arm64Asm.Cbz(0, 8));
            words.Add(Arm64Asm.B(4));
        }
        words.Add(Arm64Asm.Ret());
        return Arm64Asm.Words(words.ToArray());
    }

    private static byte[] Diamonds(int count)
    {
        var words = new List<uint>();
        for (var i = 0; i < count; i++)
        {
            words.Add(Arm64Asm.Cbz(0, 12));
            words.Add(Arm64Asm.Movz(1, 1));
            words.Add(Arm64Asm.B(8));
            words.Add(Arm64Asm.Movz(1, 2));
            words.Add(Arm64Asm.AddReg(0, 0, 1));
        }
        words.Add(Arm64Asm.Ret());
        return Arm64Asm.Words(words.ToArray());
    }
}

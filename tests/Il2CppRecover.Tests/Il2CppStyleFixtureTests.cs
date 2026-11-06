using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Il2CppRecover.Tests.Helpers;

namespace Il2CppRecover.Tests;

/// <summary>
/// Clang 18 AArch64 compiled in an IL2CPP-like shape (not Unity output, not a game).
/// Behaviour is compared with Unicorn execution of the same bytes.
/// </summary>
public class Il2CppStyleFixtureTests
{
    [Fact]
    public void Direct_thunk_and_tail_call_recover()
    {
        var late = Recover("LateUpdate");
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, late.Status);
        Assert.Contains("this.SmoothFollow();", late.CSharp);
        var tick = Recover("Tick");
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, tick.Status);
        Assert.Equal(BoundaryEvidence.ControlFlowClosed, tick.Boundary);
        Assert.Contains("this.SmoothFollow();", tick.CSharp);
    }

    [Fact]
    public void Constructor_splits_the_wide_store_and_calls_base()
    {
        var r = Recover(".ctor");
        Assert.Equal(MethodRecoveryStatus.RecoveredUnvalidated, r.Status);
        Assert.Contains("base()", r.CSharp);
        Assert.Contains("this.count = 3;", r.CSharp);
        Assert.Contains("this.speed = 5f;", r.CSharp);
    }

    [Fact]
    public void Null_checks_become_null_reference_exceptions_from_call_site_structure()
    {
        var (fx, helpers) = Helpers();
        Assert.Contains(helpers, h => h.Key == fx.Address("il2cpp_codegen_raise_null_reference_exception"));
        foreach (var name in new[] { "SumNext", "NextTotal", "CopyToNext" })
        {
            var r = Recover(name, helpers);
            Assert.True(r.Status == MethodRecoveryStatus.RecoveredUnvalidated, name + ": " + string.Join(" | ", r.Diagnostics));
            Assert.Contains("throw new System.NullReferenceException();", r.CSharp);
        }
    }

    [Fact]
    public void Every_fixture_method_matches_native_execution()
    {
        var fx = FixtureBinary.Load();
        var helpers = Infer(fx);
        var bodies = new List<(MethodContract Contract, string Body, bool Placeholder)>();
        foreach (var (symbol, contract) in FixtureContracts.Methods)
        {
            MethodBodyResult r;
            try
            {
                r = Arm64Recoverer.Recover(FixtureContracts.Request(fx, symbol, contract, helpers));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(contract.Name + " threw during recovery.", ex);
            }
            Assert.True(r.Status == MethodRecoveryStatus.RecoveredUnvalidated && !r.CSharpIsPlaceholder,
                contract.Name + ": " + string.Join(" | ", r.Diagnostics) + Environment.NewLine + r.CSharp);
            bodies.Add((contract, r.CSharp!, false));
        }
        var source = "namespace UnityEngine { public class MonoBehaviour { } }\n" + CSharpEmitter.EmitType("", "Fixture", bodies, "UnityEngine.MonoBehaviour");
        var type = RoslynRunner.Compile(source).GetType("Fixture")!;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureBinary.RepoRoot(), "fixtures", "Il2CppStyleArm64", "vectors.json")));
        var failures = new List<string>();
        var count = 0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var method = c.GetProperty("method").GetString()!;
            var native = c.GetProperty("native");
            object self;
            object? next = null;
            string? exception = null;
            object? ret = null;
            if (method == ".ctor")
                self = Activator.CreateInstance(type)!;
            else
            {
                self = RuntimeHelpers.GetUninitializedObject(type);
                if (c.GetProperty("next").ValueKind == JsonValueKind.Object)
                {
                    next = RuntimeHelpers.GetUninitializedObject(type);
                    SetFields(type, next, c.GetProperty("next"));
                }
                SetFields(type, self, c.GetProperty("this"));
                type.GetField("next")!.SetValue(self, next);
                var m = type.GetMethod(method)!;
                var args = m.GetParameters().Select((p, i) => p.ParameterType == typeof(float)
                    ? (object)c.GetProperty("floatArgs")[i].GetSingle()
                    : c.GetProperty("intArgs")[i].GetInt32()).ToArray();
                try { ret = m.Invoke(self, args); }
                catch (TargetInvocationException ex) { exception = ex.InnerException!.GetType().Name; }
            }
            var where = method + " " + c.GetProperty("this");
            var nativeException = native.TryGetProperty("exception", out var ne) ? ne.GetString() : null;
            if (nativeException != exception)
                failures.Add($"{where}: exception native={nativeException} recovered={exception}");
            if (nativeException is null && ret is int i32 && unchecked((int)native.GetProperty("x0").GetUInt64()) != i32)
                failures.Add($"{where}: return native={unchecked((int)native.GetProperty("x0").GetUInt64())} recovered={i32}");
            if (nativeException is null && ret is float f32 && BitConverter.SingleToInt32Bits(native.GetProperty("s0").GetSingle()) != BitConverter.SingleToInt32Bits(f32))
                failures.Add($"{where}: return native={native.GetProperty("s0")} recovered={f32}");
            CompareFields(type, self, native.GetProperty("this"), where + " this", failures);
            if (next is not null && native.TryGetProperty("next", out var nativeNext))
                CompareFields(type, next, nativeNext, where + " next", failures);
            count++;
        }
        Assert.True(count >= 50, "too few vectors: " + count);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(12)) + Environment.NewLine + source);
    }

    private static MethodBodyResult Recover(string name, IReadOnlyDictionary<ulong, RuntimeHelper>? helpers = null)
    {
        var fx = FixtureBinary.Load();
        helpers ??= Infer(fx);
        var (symbol, contract) = FixtureContracts.Methods.Single(m => m.Contract.Name == name);
        return Arm64Recoverer.Recover(FixtureContracts.Request(fx, symbol, contract, helpers));
    }

    private static (FixtureBinary Fx, IReadOnlyDictionary<ulong, RuntimeHelper> Helpers) Helpers()
    {
        var fx = FixtureBinary.Load();
        return (fx, Infer(fx));
    }

    private static IReadOnlyDictionary<ulong, RuntimeHelper> Infer(FixtureBinary fx)
    {
        var entries = FixtureContracts.Methods.Select(m => fx.Address(m.Symbol)).Append(fx.Address("MonoBehaviour__ctor_m")).ToList();
        var space = CodeSpace.ForElf(fx.Elf, fx.Bytes, entries);
        return RuntimeHelperInference.InferNullReferenceRaiser(entries.Select(e => FunctionDiscovery.Discover(space, e)));
    }

    private static void SetFields(Type type, object target, JsonElement state)
    {
        type.GetField("count")!.SetValue(target, state.GetProperty("count").GetInt32());
        type.GetField("speed")!.SetValue(target, state.GetProperty("speed").GetSingle());
        type.GetField("flag")!.SetValue(target, state.GetProperty("flag").GetInt32() != 0);
        type.GetField("total")!.SetValue(target, state.GetProperty("total").GetInt32());
    }

    private static void CompareFields(Type type, object actual, JsonElement expected, string where, List<string> failures)
    {
        void Check(string name, object? a, object e)
        {
            if (!Equals(a, e))
                failures.Add($"{where}: field {name} native={e} recovered={a}");
        }
        Check("count", type.GetField("count")!.GetValue(actual), expected.GetProperty("count").GetInt32());
        Check("total", type.GetField("total")!.GetValue(actual), expected.GetProperty("total").GetInt32());
        Check("flag", type.GetField("flag")!.GetValue(actual), expected.GetProperty("flag").GetInt32() != 0);
        Check("speedBits", BitConverter.SingleToInt32Bits((float)type.GetField("speed")!.GetValue(actual)!), BitConverter.SingleToInt32Bits(expected.GetProperty("speed").GetSingle()));
    }
}

using System.Text.Json;
using Il2CppRecover.Analysis;
using Il2CppRecover.Input;
using Il2CppRecover.Recovery;
using Il2CppRecover.Worker;

namespace Il2CppRecover.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (InputRejectedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return WorkerHost.ExitInput;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return WorkerHost.ExitCancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return WorkerHost.ExitAnalysis;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        return args[0] switch
        {
            "version" => Version(),
            "inventory" => Inventory(args.AsSpan(1)),
            "analyze" => Analyze(args.AsSpan(1)),
            "worker" => Worker(args.AsSpan(1)),
            "recover-slice" => RecoverSlice(args.AsSpan(1)),
            _ => Unknown(args[0])
        };
    }

    private static int Version()
    {
        Console.WriteLine(ToolInfo.Name + " " + ToolInfo.Version);
        Console.WriteLine("Cpp2IL " + ToolInfo.Cpp2IlTag + " " + ToolInfo.Cpp2IlCommit);
        Console.WriteLine("Disarm " + ToolInfo.DisarmVersion);
        return 0;
    }

    private static int Inventory(ReadOnlySpan<string> args)
    {
        if (!TryPaths(args, out var input, out var output, out var abi, out var unity))
            return 2;
        var inventory = ApkArchive.Inspect(input, output, new ExtractionLimits(), abi, unity);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "inventory.json"), JsonDefaults.Serialize(inventory));
        Console.WriteLine(JsonDefaults.Serialize(new
        {
            inventory.Succeeded,
            inventory.ScriptingBackend,
            unityVersion = inventory.UnityVersion.Version,
            unitySource = inventory.UnityVersion.Source,
            metadataVersion = inventory.Metadata?.HeaderVersion,
            abi = inventory.SelectedAbi,
            architecture = inventory.SelectedBinary?.Architecture.ToString(),
            sha256 = inventory.InputSha256,
            diagnostics = inventory.Diagnostics
        }));
        return inventory.Succeeded ? 0 : WorkerHost.ExitInput;
    }

    private static int Analyze(ReadOnlySpan<string> args)
    {
        if (!TryPaths(args, out var input, out var output, out var abi, out var unity))
            return 2;
        var timeout = TimeSpan.FromSeconds(GetInt(args, "--timeout", 180));
        var inProcess = Has(args, "--in-process");
        var gameplay = Has(args, "--gameplay-first");
        var budgetMs = GetInt(args, "--method-budget-ms", 0);
        if (inProcess)
            return AnalyzeInProcess(input, output, abi, unity, gameplay, budgetMs);

        var requestPath = Path.Combine(output, "worker-request.json");
        Directory.CreateDirectory(output);
        File.WriteAllText(requestPath, JsonSerializer.Serialize(new WorkerRequest(input, output, abi, unity, gameplay, budgetMs)));
        var dll = typeof(Program).Assembly.Location;
        if (string.IsNullOrEmpty(dll))
        {
            Console.Error.WriteLine("Process isolation is unavailable because the CLI assembly location is empty. Re-run with --in-process.");
            return WorkerHost.ExitAnalysis;
        }

        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(dll) ?? Environment.CurrentDirectory
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("worker");
        start.ArgumentList.Add("--request");
        start.ArgumentList.Add(requestPath);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        var code = WorkerHost.Run(start, timeout, cancel.Token, out var stdout, out var stderr);
        if (!string.IsNullOrWhiteSpace(stdout))
            Console.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(stderr);
        if (code == WorkerHost.ExitTimeout)
            Console.Error.WriteLine("The worker exceeded " + timeout + " and was stopped.");
        return code;
    }

    private static int Worker(ReadOnlySpan<string> args)
    {
        var requestPath = Get(args, "--request");
        if (requestPath is null || !File.Exists(requestPath))
        {
            Console.Error.WriteLine("worker requires --request path.");
            return 2;
        }
        var request = JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(requestPath)) ?? throw new InvalidOperationException("The worker request is empty.");
        return AnalyzeInProcess(request.Input, request.Output, request.Abi, request.UnityVersion, request.GameplayFirst, request.MethodBudgetMs);
    }

    private static int AnalyzeInProcess(string input, string output, string? abi, string? unity, bool gameplayFirst, int methodBudgetMs)
    {
        var budget = methodBudgetMs > 0
            ? new AnalysisBudget { MethodDeadline = TimeSpan.FromMilliseconds(methodBudgetMs) }
            : AnalysisBudget.Default;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        var result = AnalysisPipeline.Analyze(input, output, new AnalysisOptions
        {
            RequestedAbi = abi,
            SuppliedUnityVersion = unity,
            GameplayFirst = gameplayFirst,
            Budget = budget
        }, new StderrProgress(), cancel.Token);
        Console.WriteLine(JsonDefaults.Serialize(new
        {
            result.Inventory.Succeeded,
            result.Inventory.ScriptingBackend,
            unityVersion = result.Inventory.UnityVersion.Version,
            metadataVersion = result.Inventory.Metadata?.HeaderVersion,
            methods = result.Methods.Count,
            fullyRecoveredUniqueBodies = result.Report.Counts.FullyRecoveredUniqueBodies,
            metadataOnly = result.Report.Counts.MetadataOnly,
            engine = result.EngineLoadDiagnostic,
            output = result.OutputDirectory
        }));
        return result.Inventory.Succeeded ? 0 : WorkerHost.ExitInput;
    }

    private static string FormatProgress(ProgressEvent e)
    {
        if (e.Kind == "method-start")
            return "Method start: " + e.Detail;
        if (e.Kind == "heartbeat")
            return "Heartbeat " + e.Stage + (string.IsNullOrEmpty(e.Detail) ? "" : ": " + e.Detail);
        if (e.Kind == "helper")
        {
            var helper = e.Stage;
            if (e.Completed is int helperDone && e.Total is int helperTotal)
                helper += " " + helperDone + "/" + helperTotal;
            if (!string.IsNullOrEmpty(e.Detail))
                helper += ": " + e.Detail;
            return "Helpers " + helper;
        }
        if (e.Kind == "identity")
            return "Identity " + e.Detail;
        var text = e.Stage;
        if (e.Completed is int done && e.Total is int total)
            text += " " + done + "/" + total;
        if (!string.IsNullOrEmpty(e.Detail))
            text += ": " + e.Detail;
        return text;
    }

    private static int RecoverSlice(ReadOnlySpan<string> args)
    {
        var codePath = Get(args, "--code");
        var output = Get(args, "--output");
        var name = Get(args, "--name");
        var type = Get(args, "--type");
        if (codePath is null || output is null || name is null || type is null)
        {
            Console.Error.WriteLine("recover-slice requires --code, --output, --name and --type.");
            return 2;
        }
        var address = Convert.ToUInt64(Get(args, "--address") ?? "0", 16);
        var contract = new MethodContract
        {
            Name = name,
            DeclaringType = type,
            Namespace = Get(args, "--namespace") ?? "",
            IsStatic = Has(args, "--static"),
            ReturnType = ParseKind(Get(args, "--return") ?? "int32"),
            Parameters = ParseParameters(Get(args, "--params"))
        };
        var result = Arm64Recoverer.Recover(new RecoveryRequest
        {
            Code = File.ReadAllBytes(codePath),
            VirtualAddress = address,
            Contract = contract,
            Boundary = BoundaryEvidence.CallerSuppliedSlice
        });
        OutputWriter.WriteSlice(output, contract, result);
        Console.WriteLine(result.Status + (result.CSharpIsPlaceholder ? " placeholder" : " body"));
        return result.Status == MethodRecoveryStatus.RecoveredUnvalidated ? 0 : WorkerHost.ExitAnalysis;
    }

    private static List<ParameterContract> ParseParameters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var list = new List<ParameterContract>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':', 2);
            list.Add(new ParameterContract
            {
                Name = bits[0],
                Type = ParseKind(bits.Length == 2 ? bits[1] : "int32")
            });
        }
        return list;
    }

    private static PrimitiveKind ParseKind(string text) => text.ToLowerInvariant() switch
    {
        "void" => PrimitiveKind.Void,
        "bool" or "boolean" => PrimitiveKind.Boolean,
        "int" or "int32" => PrimitiveKind.Int32,
        "uint" or "uint32" => PrimitiveKind.UInt32,
        "long" or "int64" => PrimitiveKind.Int64,
        "ulong" or "uint64" => PrimitiveKind.UInt64,
        "float" => PrimitiveKind.Single,
        "double" => PrimitiveKind.Double,
        _ => throw new InputRejectedException("Unknown primitive '" + text + "'.")
    };

    private static bool TryPaths(ReadOnlySpan<string> args, out string input, out string output, out string? abi, out string? unity)
    {
        input = "";
        output = "";
        abi = Get(args, "--abi");
        unity = Get(args, "--unity-version");
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    i++;
                continue;
            }
            positional.Add(args[i]);
        }
        output = Get(args, "--output") ?? Get(args, "-o") ?? "";
        if (positional.Count < 1 || output.Length == 0)
        {
            Console.Error.WriteLine("A path and --output are required.");
            return false;
        }
        input = positional[0];
        return true;
    }

    private static string? Get(ReadOnlySpan<string> args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    private static int GetInt(ReadOnlySpan<string> args, string name, int fallback) =>
        int.TryParse(Get(args, name), out var value) ? value : fallback;

    private static bool Has(ReadOnlySpan<string> args, string name)
    {
        foreach (var arg in args)
        {
            if (arg == name)
                return true;
        }
        return false;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine("Unknown command '" + command + "'.");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Il2CppRecover commands:
              version
              inventory <apk> --output <dir> [--abi arm64-v8a] [--unity-version 2022.3.62f3]
              analyze <apk> --output <dir> [--timeout 180] [--in-process] [--abi ...] [--unity-version ...] [--gameplay-first] [--method-budget-ms 15000]
              recover-slice --code <bin> --address <hex> --name <method> --type <type> --output <dir>
                            [--namespace ...] [--static] [--return int32] [--params a:int32,b:int32]
              worker --request <json>

            analyze runs in a separate process unless --in-process is set.
            Recovered C# is an analysis view, not a Unity project.
            """);
    }

    private sealed record WorkerRequest(string Input, string Output, string? Abi, string? UnityVersion, bool GameplayFirst = false, int MethodBudgetMs = 0);

    private sealed class StderrProgress : IProgress<ProgressEvent>
    {
        public void Report(ProgressEvent value) => Console.Error.WriteLine(FormatProgress(value));
    }
}

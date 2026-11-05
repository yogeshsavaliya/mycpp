using System.Diagnostics;
using System.Text;

namespace Il2CppRecover.Worker;

public static class WorkerHost
{
    public const int ExitOk = 0;
    public const int ExitInput = 3;
    public const int ExitAnalysis = 4;
    public const int ExitTimeout = 5;
    public const int ExitCancelled = 6;

    public static int Run(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken, out string stdout, out string stderr)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };
        if (!process.Start())
            throw new InvalidOperationException("The worker process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            process.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            stdout = output.ToString();
            stderr = error.ToString();
            return cancellationToken.IsCancellationRequested ? ExitCancelled : ExitTimeout;
        }

        process.WaitForExit();
        stdout = output.ToString();
        stderr = error.ToString();
        return process.ExitCode;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

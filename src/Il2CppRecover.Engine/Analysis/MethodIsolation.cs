using Il2CppRecover.Recovery;

namespace Il2CppRecover.Analysis;

public static class MethodIsolation
{
    public static MethodBodyResult TryRecover(Func<MethodBodyResult> recover, out string? stack)
    {
        try
        {
            stack = null;
            return recover();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AnalysisLimitException ex)
        {
            stack = ex.ToString();
            return new MethodBodyResult
            {
                Status = ex.Status,
                Diagnostics = [ex.Message],
                Instructions = [],
                CSharp = CSharpEmitter.Placeholder(ex.Status, [ex.Message]),
                CSharpIsPlaceholder = true,
                Engine = "isolated"
            };
        }
        catch (Exception ex)
        {
            stack = ex.ToString();
            var diagnostic = "InternalError: " + ex.GetType().Name + ": " + ex.Message;
            return new MethodBodyResult
            {
                Status = MethodRecoveryStatus.Failed,
                Diagnostics = [diagnostic],
                Instructions = [],
                CSharp = CSharpEmitter.Placeholder(MethodRecoveryStatus.Failed, [diagnostic]),
                CSharpIsPlaceholder = true,
                Engine = "isolated"
            };
        }
    }
}

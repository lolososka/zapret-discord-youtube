namespace ZapretGui.Core;

public sealed record OwnedProcessStopResult(bool ConfirmedExit, string? Error);

/// <summary>
/// A failed Kill or timed-out wait is not proof of exit. Keep ownership until the
/// final process-state check confirms termination. Callbacks make this policy
/// testable without starting winws or touching the network.
/// </summary>
public static class OwnedProcessStopper
{
    public static async Task<OwnedProcessStopResult> StopAsync(
        Func<bool> hasExited,
        Action kill,
        Func<CancellationToken, Task> waitForExit,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(kill);
        ArgumentNullException.ThrowIfNull(waitForExit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        string? error = null;
        try
        {
            if (hasExited())
                return new OwnedProcessStopResult(true, null);
            kill();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await waitForExit(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            error = "Истекло время ожидания завершения процесса.";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        try
        {
            if (hasExited())
                return new OwnedProcessStopResult(true, null);
        }
        catch (Exception ex)
        {
            error = "Не удалось проверить завершение процесса: " + ex.Message;
        }

        return new OwnedProcessStopResult(false,
            error ?? "Процесс продолжает работать после команды остановки.");
    }
}

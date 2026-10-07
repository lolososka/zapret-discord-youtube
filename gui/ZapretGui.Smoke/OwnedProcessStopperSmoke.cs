using ZapretGui.Core;

namespace ZapretGui.Smoke;

internal static class OwnedProcessStopperSmoke
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition)
                throw new InvalidOperationException(description);
            checks++;
        }

        static OwnedProcessStopResult Stop(
            Func<bool> hasExited,
            Action kill,
            Func<CancellationToken, Task> wait,
            int timeoutMs = 1000)
            => OwnedProcessStopper.StopAsync(hasExited, kill, wait, timeoutMs)
                .GetAwaiter().GetResult();

        int kills = 0;
        int waits = 0;
        var alreadyExited = Stop(() => true, () => kills++, _ =>
        {
            waits++;
            return Task.CompletedTask;
        });
        Check(alreadyExited.ConfirmedExit && kills == 0 && waits == 0,
            "An already-exited process must not be killed or waited on again.");

        bool exited = false;
        var normal = Stop(() => exited, () => exited = true, _ => Task.CompletedTask);
        Check(normal.ConfirmedExit && normal.Error is null,
            "Confirmed normal termination must succeed.");

        var denied = Stop(() => false,
            () => throw new UnauthorizedAccessException("Access denied"),
            _ => Task.CompletedTask);
        Check(!denied.ConfirmedExit && denied.Error == "Access denied",
            "A denied Kill must retain ownership rather than report success.");

        var timedOut = Stop(() => false, () => { },
            token => Task.Delay(Timeout.Infinite, token), timeoutMs: 10);
        Check(!timedOut.ConfirmedExit && !string.IsNullOrWhiteSpace(timedOut.Error),
            "A timed-out wait must not authorize disposal or replacement.");

        exited = false;
        var exitedAfterDeniedKill = Stop(() => exited,
            () => throw new UnauthorizedAccessException("Access denied"), _ =>
            {
                exited = true;
                return Task.CompletedTask;
            });
        Check(exitedAfterDeniedKill.ConfirmedExit,
            "A process that exits naturally after Kill fails may safely be released.");

        exited = false;
        var exitedAfterWaitError = Stop(() => exited, () => exited = true,
            _ => Task.FromException(new InvalidOperationException("Wait failed")));
        Check(exitedAfterWaitError.ConfirmedExit,
            "A final confirmed exit remains valid even if waiting failed.");

        var inaccessible = Stop(
            () => throw new InvalidOperationException("State unavailable"),
            () => { }, _ => Task.CompletedTask);
        Check(!inaccessible.ConfirmedExit &&
              inaccessible.Error?.Contains("State unavailable", StringComparison.Ordinal) == true,
            "An unreadable process state must fail closed and keep the process handle.");

        var stillRunning = Stop(() => false, () => { }, _ => Task.CompletedTask);
        Check(!stillRunning.ConfirmedExit,
            "A returned wait is not sufficient proof that the process has exited.");

        exited = false;
        var firstAttempt = Stop(() => exited, () => { }, _ => Task.CompletedTask);
        var retry = Stop(() => exited, () => exited = true, _ => Task.CompletedTask);
        Check(!firstAttempt.ConfirmedExit && retry.ConfirmedExit,
            "A failed stop must remain retryable using the same owned process.");

        return checks;
    }
}

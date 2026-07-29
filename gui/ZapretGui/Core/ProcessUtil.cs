using System.Diagnostics;
using System.Text;

namespace ZapretGui.Core;

/// <summary>Результат запуска внешнего процесса.</summary>
public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string All => (StdOut + Environment.NewLine + StdErr).Trim();
}

/// <summary>
/// Запуск консольных утилит без окна. Ни один метод не бросает исключений.
/// Не ссылается ни на какие другие типы проекта.
/// </summary>
public static class ProcessUtil
{
    private const int FailedExitCode = -1;
    private const int TimeoutExitCode = -2;

    public static async Task<ProcResult> RunAsync(
        string exe,
        string args,
        string? workingDir = null,
        int timeoutMs = 60000,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(workingDir))
            psi.WorkingDirectory = workingDir;

        return await ExecuteAsync(psi, timeoutMs, ct).ConfigureAwait(false);
    }

    /// <summary>cmd.exe /d /c chcp 65001&gt;nul &amp; &lt;commandLine&gt;</summary>
    public static Task<ProcResult> ShellAsync(string commandLine, int timeoutMs = 60000, CancellationToken ct = default)
    {
        var comspec = SafeEnv("ComSpec");
        if (string.IsNullOrWhiteSpace(comspec))
            comspec = "cmd.exe";

        // chcp внутри той же оболочки, чтобы вывод пришёл в UTF-8
        var args = "/d /c chcp 65001>nul & " + commandLine;
        return RunAsync(comspec, args, null, timeoutMs, ct);
    }

    public static Task<ProcResult> ScAsync(string args, CancellationToken ct = default)
        => ShellAsync("sc " + args, 60000, ct);

    public static Task<ProcResult> PowerShellAsync(string script, int timeoutMs = 60000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        return ExecuteAsync(psi, timeoutMs, ct);
    }

    public static bool IsProcessRunning(string imageName) =>
        TryIsProcessRunning(imageName, out var isRunning) && isRunning;

    public static bool TryIsProcessRunning(
        string imageName,
        out bool isRunning)
    {
        isRunning = false;
        try
        {
            var name = StripExe(imageName);
            if (name.Length == 0)
                return true;

            var found = Process.GetProcessesByName(name);
            try
            {
                isRunning = found.Length > 0;
                return true;
            }
            finally
            {
                foreach (var p in found)
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            return false;
        }
    }

    public static int KillAll(string imageName)
    {
        var killed = 0;
        try
        {
            var name = StripExe(imageName);
            if (name.Length == 0)
                return 0;

            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                        killed++;
                    }
                }
                catch
                {
                    // процесс мог завершиться сам либо не хватает прав
                }
                finally
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // ignore
        }
        return killed;
    }

    private static async Task<ProcResult> ExecuteAsync(ProcessStartInfo psi, int timeoutMs, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            // отдельные TCS на каждый поток: пайпы читаются параллельно, дедлока нет
            var outDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) outDone.TrySetResult(true);
                else lock (stdout) { stdout.AppendLine(e.Data); }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) errDone.TrySetResult(true);
                else lock (stderr) { stderr.AppendLine(e.Data); }
            };

            if (!proc.Start())
                return new ProcResult(FailedExitCode, string.Empty, "Не удалось запустить процесс.");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var effectiveTimeout = timeoutMs <= 0 ? Timeout.Infinite : timeoutMs;
            var timedOut = false;

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (effectiveTimeout != Timeout.Infinite)
                    linked.CancelAfter(effectiveTimeout);

                try
                {
                    await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = !ct.IsCancellationRequested;
                    KillTree(proc);
                }
            }

            if (!timedOut && !ct.IsCancellationRequested)
            {
                // дожидаемся закрытия обоих пайпов, но не навсегда
                await Task.WhenAny(
                        Task.WhenAll(outDone.Task, errDone.Task),
                        Task.Delay(2000))
                    .ConfigureAwait(false);
            }

            int exitCode;
            if (timedOut)
            {
                exitCode = TimeoutExitCode;
            }
            else if (ct.IsCancellationRequested)
            {
                exitCode = FailedExitCode;
            }
            else
            {
                try { exitCode = proc.ExitCode; }
                catch { exitCode = FailedExitCode; }
            }

            string outText, errText;
            lock (stdout) { outText = stdout.ToString(); }
            lock (stderr) { errText = stderr.ToString(); }

            if (timedOut)
                errText = (errText.TrimEnd() + Environment.NewLine + "Превышено время ожидания процесса.").Trim();

            return new ProcResult(exitCode, outText.TrimEnd(), errText.TrimEnd());
        }
        catch (Exception ex)
        {
            return new ProcResult(FailedExitCode, string.Empty, ex.Message);
        }
        finally
        {
            try { proc?.Dispose(); } catch { /* ignore */ }
        }
    }

    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch
        {
            // процесс уже мёртв или недоступен
        }
    }

    private static string StripExe(string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName))
            return string.Empty;
        var name = imageName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    private static string? SafeEnv(string name)
    {
        try { return Environment.GetEnvironmentVariable(name); }
        catch { return null; }
    }
}

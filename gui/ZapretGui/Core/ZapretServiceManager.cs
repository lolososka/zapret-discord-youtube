using System.Text;

namespace ZapretGui.Core;

public enum ServiceState { NotInstalled, Stopped, Running, Pending, Unknown }

public sealed record CommandResult(bool Success, string Output);

/// <summary>
/// Управление службой zapret — порт секций :service_install / :service_remove /
/// :service_status / :test_service / :get_strategy_name из service.bat.
/// </summary>
public static class ZapretServiceManager
{
    public const string ServiceName = "zapret";
    private const string RegistryKey = @"HKLM\System\CurrentControlSet\Services\zapret";
    private const string RegistryValue = "zapret-discord-youtube";

    public static async Task<ServiceState> QueryAsync(string serviceName = "zapret")
    {
        try
        {
            var r = await ProcessUtil.ScAsync($"query \"{serviceName}\"").ConfigureAwait(false);
            var text = r.All;

            // 1060 = ERROR_SERVICE_DOES_NOT_EXIST, sc отдаёт его и кодом возврата, и в тексте.
            if (r.ExitCode == 1060 || text.Contains("1060", StringComparison.Ordinal))
                return ServiceState.NotInstalled;

            // STATE-строка выводится ASCII-токеном независимо от кодовой страницы.
            if (text.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("PAUSE_PENDING", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("CONTINUE_PENDING", StringComparison.OrdinalIgnoreCase))
                return ServiceState.Pending;

            if (text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return ServiceState.Running;

            if (text.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("PAUSED", StringComparison.OrdinalIgnoreCase))
                return ServiceState.Stopped;

            if (!r.Success)
                return ServiceState.NotInstalled;

            return ServiceState.Unknown;
        }
        catch
        {
            return ServiceState.Unknown;
        }
    }

    public static Task<ServiceState> QueryWinDivertAsync() => QueryAsync("WinDivert");

    public static async Task<string?> InstalledStrategyNameAsync()
    {
        try
        {
            var r = await ProcessUtil.ShellAsync(
                $"reg query \"{RegistryKey}\" /v {RegistryValue}").ConfigureAwait(false);

            if (!r.Success)
                return null;

            foreach (var raw in r.All.Split('\n'))
            {
                var line = raw.Trim();
                var idx = line.IndexOf("REG_SZ", StringComparison.OrdinalIgnoreCase);
                if (idx < 0 || !line.StartsWith(RegistryValue, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = line[(idx + "REG_SZ".Length)..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<CommandResult> InstallAsync(Strategy s, GameFilterMode mode)
    {
        var log = new StringBuilder();
        try
        {
            AppPaths.EnsureUserLists();
            FeatureFlags.EnableTcpTimestamps();   // :tcp_enable перед созданием службы

            var args = StrategyParser.BuildArguments(s, mode);
            var binPath = BuildBinPath(args);

            await StopAndDeleteAsync(ServiceName, log).ConfigureAwait(false);

            var create = await ScDirectAsync(
                $"create {ServiceName} binPath= \"{binPath}\" DisplayName= \"zapret\" start= auto").ConfigureAwait(false);
            Append(log, "sc create", create);
            if (!create.Success)
                return new CommandResult(false, Finish(log, "Не удалось создать службу zapret."));

            Append(log, "sc description",
                await ScDirectAsync($"description {ServiceName} \"Zapret DPI bypass software\"").ConfigureAwait(false));

            var start = await ScDirectAsync($"start {ServiceName}").ConfigureAwait(false);
            Append(log, "sc start", start);

            Append(log, "reg add", await ProcessUtil.ShellAsync(
                $"reg add \"{RegistryKey}\" /v {RegistryValue} /t REG_SZ /d \"{s.Name}\" /f").ConfigureAwait(false));

            if (!start.Success)
                return new CommandResult(false, Finish(log, "Служба создана, но не запустилась."));

            return new CommandResult(true, Finish(log, $"Служба zapret установлена и запущена: {s.DisplayName}."));
        }
        catch (Exception ex)
        {
            return new CommandResult(false, Finish(log, "Ошибка установки службы: " + ex.Message));
        }
    }

    public static async Task<CommandResult> RemoveAsync()
    {
        var log = new StringBuilder();
        try
        {
            var state = await QueryAsync(ServiceName).ConfigureAwait(false);
            if (state == ServiceState.NotInstalled)
                log.AppendLine("Служба \"zapret\" не установлена.");
            else
                await StopAndDeleteAsync(ServiceName, log).ConfigureAwait(false);

            if (ProcessUtil.IsProcessRunning("winws.exe"))
            {
                log.AppendLine(
                    "После остановки службы ещё работает сторонний winws.exe; " +
                    "он оставлен без изменений.");
            }

            if (await QueryAsync("WinDivert").ConfigureAwait(false) != ServiceState.NotInstalled)
                await StopAndDeleteAsync("WinDivert", log).ConfigureAwait(false);

            await StopAndDeleteAsync("WinDivert14", log).ConfigureAwait(false);

            var left = await QueryAsync(ServiceName).ConfigureAwait(false);
            var ok = left == ServiceState.NotInstalled;
            return new CommandResult(ok, Finish(log, ok
                ? "Служба zapret и драйверы WinDivert удалены."
                : "Служба zapret всё ещё присутствует — возможно, требуется перезагрузка."));
        }
        catch (Exception ex)
        {
            return new CommandResult(false, Finish(log, "Ошибка удаления службы: " + ex.Message));
        }
    }

    // --- helpers -------------------------------------------------------

    /// Экранирование как в service.bat: значение binPath целиком берётся в кавычки, а все
    /// кавычки внутри (вокруг путей к спискам и .bin) превращаются в \" — SCM хранит строку
    /// как есть, а winws.exe разбирает её по обычным правилам CRT и видит исходные кавычки.
    private static string BuildBinPath(string args)
    {
        var exe = AppPaths.WinWs.Replace("\"", "\\\"");
        var inner = args.Replace("\"", "\\\"").Trim();
        return $"\\\"{exe}\\\" {inner}";
    }

    /// <summary>sc.exe напрямую, без cmd.exe: посредник переинтерпретировал бы кавычки в binPath.</summary>
    private static Task<ProcResult> ScDirectAsync(string args) =>
        ProcessUtil.RunAsync("sc.exe", args, AppPaths.Bin, 60000);

    private static async Task StopAndDeleteAsync(string name, StringBuilder log)
    {
        Append(log, $"net stop {name}", await ProcessUtil.ShellAsync($"net stop \"{name}\"", 30000).ConfigureAwait(false));
        Append(log, $"sc delete {name}", await ProcessUtil.ScAsync($"delete \"{name}\"").ConfigureAwait(false));
    }

    private static void Append(StringBuilder log, string title, ProcResult r)
    {
        var text = r.All;
        log.Append("> ").Append(title).Append(" (код ").Append(r.ExitCode).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(text))
            log.AppendLine(text);
    }

    private static string Finish(StringBuilder log, string verdict)
    {
        log.AppendLine(verdict);
        return log.ToString().Trim();
    }
}

using Microsoft.Win32;

namespace ZapretGui.Core;

public enum CheckStatus { Pending, Running, Ok, Warning, Failed, Inconclusive }

public readonly record struct CheckFixResult(bool Succeeded, string Message);

public sealed class CheckResult
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public CheckStatus Status { get; set; } = CheckStatus.Pending;
    public string Detail { get; set; } = "";
    public string? Link { get; set; }
    public string? FixLabel { get; set; }
    public Func<Task<CheckFixResult>>? Fix { get; set; }
    public bool RequiresStoppedBypass { get; set; }
}

public static class DiagnosticsRunner
{
    // Порядок и состав проверок повторяют :service_diagnostics из service.bat
    private static readonly (string Id, string Title)[] Checks =
    {
        ("bfe",             "Служба Base Filtering Engine"),
        ("proxy",           "Системный прокси"),
        ("tcp_timestamps",  "TCP timestamps"),
        ("adguard",         "Adguard"),
        ("killer",          "Службы Killer"),
        ("intel",           "Intel Connectivity Network Service"),
        ("checkpoint",      "Check Point"),
        ("smartbyte",       "SmartByte"),
        ("windivert_sys",   "Драйвер WinDivert64.sys"),
        ("vpn",             "Службы VPN"),
        ("dns",             "Защищённый DNS (DoH)"),
        ("hosts",           "Файл hosts"),
        ("windivert_orphan","Зависшая служба WinDivert"),
        ("conflicts",       "Конфликтующие обходы блокировок"),
    };

    public static IReadOnlyList<string> CheckTitles { get; } =
        Array.ConvertAll(Checks, c => c.Title);

    private const string AdguardIssue = "https://github.com/Flowseal/zapret-discord-youtube/issues/417";
    private const string KillerIssue = "https://github.com/Flowseal/zapret-discord-youtube/issues/2512#issuecomment-2821119513";
    private const string IntelIssue = "https://github.com/ValdikSS/GoodbyeDPI/issues/541#issuecomment-2661670982";

    private static readonly string[] ConflictingServices = { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" };

    public static async Task RunAllAsync(IProgress<CheckResult> progress, CancellationToken ct)
    {
        if (progress is null) return;

        // `sc query` без аргументов перечисляет только запущенные службы — как в service.bat.
        // Сохраняем весь ProcResult: ошибка запуска sc не должна выглядеть как пустой
        // (и поэтому якобы безопасный) список служб.
        var scAll = new Lazy<Task<ProcResult>>(() => ProcessUtil.ScAsync("query", ct));

        await RunOneAsync(progress, ct, 0, r => CheckBfeAsync(r, ct)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 1, r => { CheckProxy(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 2, r => CheckTimestampsAsync(r, ct)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 3, r => { CheckAdguard(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 4, r => CheckServiceInventoryAsync(r, scAll, ct, CheckKiller)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 5, r => CheckServiceInventoryAsync(r, scAll, ct, CheckIntel)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 6, r => CheckServiceInventoryAsync(r, scAll, ct, CheckCheckPoint)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 7, r => CheckServiceInventoryAsync(r, scAll, ct, CheckSmartByte)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 8, r => { CheckWinDivertSys(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 9, r => CheckServiceInventoryAsync(r, scAll, ct, CheckVpn)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 10, r => { CheckSecureDns(r, ct); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 11, r => { CheckHosts(r, ct); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 12, r => CheckOrphanWinDivertAsync(r, ct)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 13, r => CheckConflictsAsync(r, ct)).ConfigureAwait(false);
    }

    private static async Task RunOneAsync(IProgress<CheckResult> progress, CancellationToken ct, int index, Func<CheckResult, Task> body)
    {
        ct.ThrowIfCancellationRequested();

        var (id, title) = Checks[index];
        var result = new CheckResult
        {
            Id = id,
            Title = title,
            Status = CheckStatus.Running,
            Detail = "Выполняется проверка…"
        };
        progress.Report(result);

        try
        {
            await body(result).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Status = CheckStatus.Inconclusive;
            result.Detail = "Проверка отменена.";
            progress.Report(result);
            throw;
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Inconclusive;
            result.Detail = "Не удалось выполнить проверку: " + ex.Message;
        }

        progress.Report(result);
    }

    private static async Task CheckServiceInventoryAsync(
        CheckResult result,
        Lazy<Task<ProcResult>> serviceInventory,
        CancellationToken ct,
        Action<CheckResult, string> check)
    {
        var query = await serviceInventory.Value.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!query.Success)
        {
            result.Status = CheckStatus.Inconclusive;
            result.Detail = "Не удалось получить список запущенных служб: " + Shorten(query.All);
            return;
        }

        check(result, query.All);
    }

    // ── 1. Base Filtering Engine ──────────────────────────────────────────────
    private static async Task CheckBfeAsync(CheckResult r, CancellationToken ct)
    {
        var res = await ProcessUtil.ScAsync("query BFE", ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!res.Success && !IsServiceMissing(res))
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить службу Base Filtering Engine: " + Shorten(res.All);
            return;
        }

        if (res.All.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Служба Base Filtering Engine запущена.";
            return;
        }

        r.Status = CheckStatus.Failed;
        r.Detail = "Служба Base Filtering Engine не запущена. Без неё zapret работать не будет.";
        r.FixLabel = "Запустить BFE";
        r.Fix = async () =>
        {
            var start = await ProcessUtil.ScAsync("start BFE").ConfigureAwait(false);
            var check = await ProcessUtil.ScAsync("query BFE").ConfigureAwait(false);
            if (check.Success && check.All.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return FixSucceeded("Служба Base Filtering Engine запущена.");

            var evidence = !check.Success && !IsServiceMissing(check) ? check.All : start.All;
            return FixFailed("Не удалось запустить BFE: " + Shorten(evidence));
        };
    }

    // ── 2. Системный прокси ───────────────────────────────────────────────────
    private static void CheckProxy(CheckResult r)
    {
        bool enabled;
        string server;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null)
            {
                r.Status = CheckStatus.Inconclusive;
                r.Detail = "Не удалось открыть системные параметры прокси.";
                return;
            }

            enabled = Convert.ToInt64(key.GetValue("ProxyEnable", 0)) == 1;
            server = key.GetValue("ProxyServer") as string ?? "";
        }
        catch (Exception ex)
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить системный прокси: " + Shorten(ex.Message);
            return;
        }

        if (!enabled)
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Системный прокси выключен.";
            return;
        }

        r.Status = CheckStatus.Warning;
        r.Detail = string.IsNullOrWhiteSpace(server)
            ? "Системный прокси включён. Убедитесь, что он рабочий, либо отключите его, если прокси не используете."
            : $"Системный прокси включён: {server}. Убедитесь, что он рабочий, либо отключите его, если прокси не используете.";
        r.FixLabel = "Отключить прокси";
        r.Fix = () =>
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
                if (key == null)
                    return Task.FromResult(FixFailed("Не удалось открыть параметры прокси в реестре."));

                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                var stillEnabled = Convert.ToInt64(key.GetValue("ProxyEnable", 1)) == 1;
                return Task.FromResult(stillEnabled
                    ? FixFailed("Параметр системного прокси остался включён.")
                    : FixSucceeded("Системный прокси отключён."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(FixFailed("Не удалось отключить прокси: " + ex.Message));
            }
        };
    }

    // ── 3. TCP timestamps ─────────────────────────────────────────────────────
    private static async Task CheckTimestampsAsync(CheckResult r, CancellationToken ct)
    {
        var res = await ProcessUtil.ShellAsync("netsh interface tcp show global", 20000, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!res.Success)
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить TCP timestamps: " + Shorten(res.All);
            return;
        }

        if (HasEnabledTimestamps(res.All))
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "TCP timestamps включены.";
            return;
        }

        r.Status = CheckStatus.Warning;
        r.Detail = "TCP timestamps выключены. Некоторые стратегии без них работают нестабильно.";
        r.FixLabel = "Включить timestamps";
        r.Fix = async () =>
        {
            var set = await ProcessUtil.ShellAsync("netsh interface tcp set global timestamps=enabled", 20000).ConfigureAwait(false);
            var check = await ProcessUtil.ShellAsync("netsh interface tcp show global", 20000).ConfigureAwait(false);
            if (check.Success && HasEnabledTimestamps(check.All))
                return FixSucceeded("TCP timestamps успешно включены.");

            var evidence = check.Success ? set.All : check.All;
            return FixFailed("Не удалось включить TCP timestamps: " + Shorten(evidence));
        };
    }

    private static bool HasEnabledTimestamps(string netshOutput)
    {
        foreach (var line in SplitLines(netshOutput))
        {
            if (line.Contains("timestamp", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("включ", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    // ── 4. Adguard ────────────────────────────────────────────────────────────
    private static void CheckAdguard(CheckResult r)
    {
        if (!ProcessUtil.TryIsProcessRunning("AdguardSvc.exe", out var isRunning))
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить процесс AdguardSvc.exe.";
        }
        else if (isRunning)
        {
            r.Status = CheckStatus.Failed;
            r.Detail = "Обнаружен процесс Adguard (AdguardSvc.exe). Adguard может ломать работу Discord.";
            r.Link = AdguardIssue;
        }
        else
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Процессы Adguard не найдены.";
        }
    }

    // ── 5. Killer ─────────────────────────────────────────────────────────────
    private static void CheckKiller(CheckResult r, string scAll)
    {
        if (ContainsToken(scAll, "Killer"))
        {
            r.Status = CheckStatus.Failed;
            r.Detail = "Найдены службы Killer. Они конфликтуют с zapret — отключите их в services.msc или удалите пакет Killer.";
            r.Link = KillerIssue;
        }
        else
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Службы Killer не найдены.";
        }
    }

    // ── 6. Intel Connectivity Network Service ─────────────────────────────────
    private static void CheckIntel(CheckResult r, string scAll)
    {
        var found = SplitLines(scAll).Any(l =>
            l.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
            l.Contains("Connectivity", StringComparison.OrdinalIgnoreCase) &&
            l.Contains("Network", StringComparison.OrdinalIgnoreCase));

        if (found)
        {
            r.Status = CheckStatus.Failed;
            r.Detail = "Найдена служба Intel Connectivity Network Service. Она конфликтует с zapret.";
            r.Link = IntelIssue;
        }
        else
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Служба Intel Connectivity Network Service не найдена.";
        }
    }

    // ── 7. Check Point ────────────────────────────────────────────────────────
    private static void CheckCheckPoint(CheckResult r, string scAll)
    {
        if (ContainsToken(scAll, "TracSrvWrapper") || ContainsToken(scAll, "EPWD"))
        {
            r.Status = CheckStatus.Failed;
            r.Detail = "Найдены службы Check Point. Check Point конфликтует с zapret — попробуйте удалить его.";
        }
        else
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Службы Check Point не найдены.";
        }
    }

    // ── 8. SmartByte ──────────────────────────────────────────────────────────
    private static void CheckSmartByte(CheckResult r, string scAll)
    {
        if (ContainsToken(scAll, "SmartByte"))
        {
            r.Status = CheckStatus.Failed;
            r.Detail = "Найдены службы SmartByte. Они конфликтуют с zapret — удалите SmartByte или отключите его через services.msc.";
        }
        else
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Службы SmartByte не найдены.";
        }
    }

    // ── 9. WinDivert64.sys ────────────────────────────────────────────────────
    private static void CheckWinDivertSys(CheckResult r)
    {
        var path = Path.Combine(AppPaths.Bin, "WinDivert64.sys");
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new FileNotFoundException("Вместо файла найдена папка.", path);

            r.Status = CheckStatus.Ok;
            r.Detail = "Драйвер WinDivert64.sys найден.";
        }
        catch (FileNotFoundException)
        {
            r.Status = CheckStatus.Failed;
            r.Detail = "Файл WinDivert64.sys не найден в папке bin. Скорее всего его удалил антивирус — восстановите файл и добавьте папку в исключения.";
        }
        catch (DirectoryNotFoundException)
        {
            r.Status = CheckStatus.Failed;
            r.Detail = "Файл WinDivert64.sys не найден в папке bin. Скорее всего его удалил антивирус — восстановите файл и добавьте папку в исключения.";
        }
        catch (Exception ex)
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить WinDivert64.sys: " + Shorten(ex.Message);
        }
    }

    // ── 10. VPN ───────────────────────────────────────────────────────────────
    private static void CheckVpn(CheckResult r, string scAll)
    {
        var names = new List<string>();
        foreach (var line in SplitLines(scAll))
        {
            if (!line.Contains("VPN", StringComparison.OrdinalIgnoreCase)) continue;
            var idx = line.IndexOf(':');
            var name = (idx >= 0 ? line[(idx + 1)..] : line).Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        if (names.Count > 0)
        {
            r.Status = CheckStatus.Warning;
            r.Detail = "Найдены службы VPN: " + string.Join(", ", names) +
                       ". Некоторые VPN конфликтуют с zapret — убедитесь, что все они отключены.";
        }
        else
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Активные службы VPN не найдены.";
        }
    }

    // ── 11. Защищённый DNS ────────────────────────────────────────────────────
    private static void CheckSecureDns(
        CheckResult r,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var root = Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters");
            var found = false;
            if (root is not null &&
                !TryScanDohFlags(root, 0, ct, out found))
            {
                r.Status = CheckStatus.Inconclusive;
                r.Detail = "Не удалось полностью прочитать параметры защищённого DNS.";
                return;
            }

            if (root is not null && found)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "В системе настроен зашифрованный DNS (DoH).";
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить защищённый DNS: " + Shorten(ex.Message);
            return;
        }

        r.Status = CheckStatus.Warning;
        r.Detail = "Зашифрованный DNS не обнаружен. Настройте DNS-over-HTTPS в браузере со сторонним DNS-провайдером; " +
                   "в Windows 11 это можно включить в параметрах адаптера, тогда предупреждение исчезнет.";
    }

    private static bool TryScanDohFlags(
        RegistryKey key,
        int depth,
        CancellationToken ct,
        out bool found)
    {
        found = false;
        if (depth > 8)
            return true;
        try
        {
            ct.ThrowIfCancellationRequested();
            foreach (var valueName in key.GetValueNames())
            {
                if (!string.Equals(valueName, "DohFlags", StringComparison.OrdinalIgnoreCase))
                    continue;
                var raw = key.GetValue(valueName);
                if (raw is not null && Convert.ToInt64(raw) > 0)
                {
                    found = true;
                    return true;
                }
            }

            foreach (var subName in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var sub = key.OpenSubKey(subName);
                if (sub is null)
                    continue;
                if (!TryScanDohFlags(sub, depth + 1, ct, out var nested))
                    return false;
                if (nested)
                {
                    found = true;
                    return true;
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    // ── 12. hosts ─────────────────────────────────────────────────────────────
    private static void CheckHosts(
        CheckResult r,
        CancellationToken ct)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\drivers\etc\hosts");

        if (!File.Exists(path))
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Файл hosts не найден или недоступен — записи проверить не удалось.";
            return;
        }

        var hits = new List<string>();
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                    hits.Add(line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось прочитать файл hosts: " + ex.Message;
            return;
        }

        if (hits.Count > 0)
        {
            r.Status = CheckStatus.Warning;
            r.Detail = $"В файле hosts есть записи для youtube.com или youtu.be ({hits.Count} шт.). " +
                       "Это может мешать доступу к YouTube.";
        }
        else
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Записей для YouTube в файле hosts нет.";
        }
    }

    // ── 13. Зависшая служба WinDivert ─────────────────────────────────────────
    private static async Task CheckOrphanWinDivertAsync(CheckResult r, CancellationToken ct)
    {
        if (!ProcessUtil.TryIsProcessRunning("winws.exe", out var winwsRunning))
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить, запущен ли winws.exe.";
            return;
        }

        var divert = await ProcessUtil.ScAsync("query \"WinDivert\"", ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!divert.Success && !IsServiceMissing(divert))
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить службу WinDivert: " + Shorten(divert.All);
            return;
        }

        var divertActive = divert.All.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                           divert.All.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase);

        if (winwsRunning)
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "winws.exe запущен — служба WinDivert используется по назначению.";
            return;
        }

        if (!divertActive)
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Зависшей службы WinDivert нет.";
            return;
        }

        r.Status = CheckStatus.Warning;
        r.Detail = "winws.exe не запущен, но служба WinDivert активна. Это остаток от прошлого запуска — её нужно удалить.";
        r.FixLabel = "Удалить WinDivert";
        r.RequiresStoppedBypass = true;
        r.Fix = async () =>
        {
            var safety = EnsureWinwsStopped();
            if (safety is not null)
                return safety.Value;

            await ProcessUtil.ShellAsync("net stop \"WinDivert\"").ConfigureAwait(false);
            await ProcessUtil.ScAsync("delete \"WinDivert\"").ConfigureAwait(false);

            var after = await ProcessUtil.ScAsync("query \"WinDivert\"").ConfigureAwait(false);
            if (IsServiceMissing(after))
                return FixSucceeded("Служба WinDivert успешно удалена.");

            if (!after.Success)
                return FixFailed("Не удалось проверить удаление WinDivert: " + Shorten(after.All));

            // Не удалилась — виноват другой обход, использующий тот же драйвер
            var conflicts = await FindConflictingAsync(CancellationToken.None).ConfigureAwait(false);
            if (!conflicts.Succeeded)
                return FixFailed("Не удалось проверить конфликтующие службы: " + conflicts.Error);

            safety = EnsureWinwsStopped();
            if (safety is not null)
                return safety.Value;

            var removed = await RemoveConflictingAsync(conflicts.Found).ConfigureAwait(false);

            safety = EnsureWinwsStopped();
            if (safety is not null)
                return safety.Value;

            await ProcessUtil.ShellAsync("net stop \"WinDivert\"").ConfigureAwait(false);
            await ProcessUtil.ScAsync("delete \"WinDivert\"").ConfigureAwait(false);
            var final = await ProcessUtil.ScAsync("query \"WinDivert\"").ConfigureAwait(false);

            if (IsServiceMissing(final))
                return FixSucceeded(removed.Count > 0
                    ? "Служба WinDivert удалена после удаления конфликтующих служб (" + string.Join(", ", removed) + ")."
                    : "Служба WinDivert успешно удалена.");

            if (!final.Success)
                return FixFailed("Не удалось проверить удаление WinDivert: " + Shorten(final.All));

            return FixFailed(removed.Count > 0
                ? "WinDivert всё ещё не удаляется. Удалены конфликтующие службы: " + string.Join(", ", removed) +
                  ". Проверьте вручную, какой ещё обход блокировок использует WinDivert."
                : "Конфликтующих служб не найдено, но WinDivert не удаляется. Проверьте вручную, какой обход блокировок его использует.");
        };
    }

    // ── 14. Конфликтующие обходы ──────────────────────────────────────────────
    private static async Task CheckConflictsAsync(CheckResult r, CancellationToken ct)
    {
        var search = await FindConflictingAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (!search.Succeeded)
        {
            r.Status = CheckStatus.Inconclusive;
            r.Detail = "Не удалось проверить конфликтующие службы: " + search.Error;
            return;
        }

        var found = search.Found;

        if (found.Count == 0)
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Конфликтующие службы обхода блокировок не найдены.";
            return;
        }

        r.Status = CheckStatus.Failed;
        r.Detail = "Найдены конфликтующие службы обхода блокировок: " + string.Join(", ", found) +
                   ". Они мешают zapret и их следует удалить.";
        r.FixLabel = "Удалить конфликтующие службы";
        r.RequiresStoppedBypass = true;
        r.Fix = async () =>
        {
            var safety = EnsureWinwsStopped();
            if (safety is not null)
                return safety.Value;

            var again = await FindConflictingAsync(CancellationToken.None).ConfigureAwait(false);
            if (!again.Succeeded)
                return FixFailed("Не удалось повторно проверить конфликтующие службы: " + again.Error);

            if (again.Found.Count == 0)
                return FixSucceeded("Конфликтующих служб больше нет.");

            safety = EnsureWinwsStopped();
            if (safety is not null)
                return safety.Value;

            var removed = await RemoveConflictingAsync(again.Found).ConfigureAwait(false);

            safety = EnsureWinwsStopped();
            if (safety is not null)
                return safety.Value;

            foreach (var name in new[] { "WinDivert", "WinDivert14" })
            {
                await ProcessUtil.ShellAsync($"net stop \"{name}\"").ConfigureAwait(false);
                await ProcessUtil.ScAsync($"delete \"{name}\"").ConfigureAwait(false);
            }

            var verification = await FindConflictingAsync(CancellationToken.None).ConfigureAwait(false);
            if (!verification.Succeeded)
                return FixFailed("Не удалось проверить результат удаления: " + verification.Error);

            if (verification.Found.Count == 0)
                return FixSucceeded(removed.Count > 0
                    ? "Удалены конфликтующие службы: " + string.Join(", ", removed) + "."
                    : "Конфликтующих служб больше нет.");

            return FixFailed("Удалены службы: " + (removed.Count > 0 ? string.Join(", ", removed) : "нет") +
                             ". Не удалось удалить: " + string.Join(", ", verification.Found) + ".");
        };
    }

    private static async Task<ConflictingSearchResult> FindConflictingAsync(CancellationToken ct)
    {
        var found = new List<string>();
        var errors = new List<string>();
        foreach (var name in ConflictingServices)
        {
            ct.ThrowIfCancellationRequested();
            var res = await ProcessUtil.ScAsync($"query \"{name}\"", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (res.Success)
                found.Add(name);
            else if (!IsServiceMissing(res))
                errors.Add(name + ": " + Shorten(res.All, 100));
        }

        return errors.Count == 0
            ? new ConflictingSearchResult(true, found, string.Empty)
            : new ConflictingSearchResult(false, found, string.Join("; ", errors));
    }

    private static async Task<List<string>> RemoveConflictingAsync(IEnumerable<string> services)
    {
        var removed = new List<string>();
        foreach (var name in services)
        {
            try
            {
                await ProcessUtil.ShellAsync($"net stop \"{name}\"").ConfigureAwait(false);
                await ProcessUtil.ScAsync($"delete \"{name}\"").ConfigureAwait(false);
                var check = await ProcessUtil.ScAsync($"query \"{name}\"").ConfigureAwait(false);
                if (IsServiceMissing(check)) removed.Add(name);
            }
            catch { }
        }
        return removed;
    }

    // ── Вспомогательное ───────────────────────────────────────────────────────
    private readonly record struct ConflictingSearchResult(
        bool Succeeded,
        IReadOnlyList<string> Found,
        string Error);

    private static CheckFixResult FixSucceeded(string message) => new(true, message);

    private static CheckFixResult FixFailed(string message) => new(false, message);

    private static CheckFixResult? EnsureWinwsStopped()
    {
        if (!ProcessUtil.TryIsProcessRunning("winws.exe", out var running))
        {
            return FixFailed(
                "Не удалось безопасно проверить winws.exe. Удаление служб отменено.");
        }

        return running
            ? FixFailed(
                "Обнаружен работающий winws.exe. Сначала остановите все обходы и повторите исправление.")
            : null;
    }

    private static bool IsServiceMissing(ProcResult result) =>
        !result.Success && result.All.Contains("1060", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitLines(string text) =>
        string.IsNullOrEmpty(text)
            ? Enumerable.Empty<string>()
            : text.Split('\n').Select(l => l.TrimEnd('\r'));

    private static bool ContainsToken(string haystack, string token) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return "нет вывода";
        var one = string.Join(" ", SplitLines(text).Select(l => l.Trim()).Where(l => l.Length > 0));
        return one.Length <= max ? one : one[..max] + "…";
    }
}

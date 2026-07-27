using Microsoft.Win32;

namespace ZapretGui.Core;

public enum CheckStatus { Pending, Running, Ok, Warning, Failed }

public sealed class CheckResult
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public CheckStatus Status { get; set; } = CheckStatus.Pending;
    public string Detail { get; set; } = "";
    public string? Link { get; set; }
    public string? FixLabel { get; set; }
    public Func<Task<string>>? Fix { get; set; }
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

        // `sc query` без аргументов перечисляет только запущенные службы — как в service.bat
        var scAll = new Lazy<Task<string>>(async () =>
        {
            try { return (await ProcessUtil.ScAsync("query", ct).ConfigureAwait(false)).All; }
            catch { return string.Empty; }
        });

        await RunOneAsync(progress, ct, 0, r => CheckBfeAsync(r, ct)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 1, r => { CheckProxy(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 2, r => CheckTimestampsAsync(r, ct)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 3, r => { CheckAdguard(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 4, async r => CheckKiller(r, await scAll.Value.ConfigureAwait(false))).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 5, async r => CheckIntel(r, await scAll.Value.ConfigureAwait(false))).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 6, async r => CheckCheckPoint(r, await scAll.Value.ConfigureAwait(false))).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 7, async r => CheckSmartByte(r, await scAll.Value.ConfigureAwait(false))).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 8, r => { CheckWinDivertSys(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 9, async r => CheckVpn(r, await scAll.Value.ConfigureAwait(false))).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 10, r => { CheckSecureDns(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 11, r => { CheckHosts(r); return Task.CompletedTask; }).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 12, r => CheckOrphanWinDivertAsync(r, ct)).ConfigureAwait(false);
        await RunOneAsync(progress, ct, 13, r => CheckConflictsAsync(r, ct)).ConfigureAwait(false);
    }

    private static async Task RunOneAsync(IProgress<CheckResult> progress, CancellationToken ct, int index, Func<CheckResult, Task> body)
    {
        if (ct.IsCancellationRequested) return;

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
        }
        catch (OperationCanceledException)
        {
            result.Status = CheckStatus.Warning;
            result.Detail = "Проверка отменена.";
        }
        catch (Exception ex)
        {
            result.Status = CheckStatus.Warning;
            result.Detail = "Не удалось выполнить проверку: " + ex.Message;
        }

        progress.Report(result);
    }

    // ── 1. Base Filtering Engine ──────────────────────────────────────────────
    private static async Task CheckBfeAsync(CheckResult r, CancellationToken ct)
    {
        var res = await ProcessUtil.ScAsync("query BFE", ct).ConfigureAwait(false);
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
            return check.All.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
                ? "Служба Base Filtering Engine запущена."
                : "Не удалось запустить BFE: " + Shorten(start.All);
        };
    }

    // ── 2. Системный прокси ───────────────────────────────────────────────────
    private static void CheckProxy(CheckResult r)
    {
        var enabled = false;
        string server = "";

        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
        {
            if (key != null)
            {
                try { enabled = Convert.ToInt64(key.GetValue("ProxyEnable", 0)) == 1; } catch { }
                server = key.GetValue("ProxyServer") as string ?? "";
            }
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
                if (key == null) return Task.FromResult("Не удалось открыть параметры прокси в реестре.");
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                return Task.FromResult("Системный прокси отключён.");
            }
            catch (Exception ex)
            {
                return Task.FromResult("Не удалось отключить прокси: " + ex.Message);
            }
        };
    }

    // ── 3. TCP timestamps ─────────────────────────────────────────────────────
    private static async Task CheckTimestampsAsync(CheckResult r, CancellationToken ct)
    {
        var res = await ProcessUtil.ShellAsync("netsh interface tcp show global", 20000, ct).ConfigureAwait(false);
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
            return HasEnabledTimestamps(check.All)
                ? "TCP timestamps успешно включены."
                : "Не удалось включить TCP timestamps: " + Shorten(set.All);
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
        if (ProcessUtil.IsProcessRunning("AdguardSvc.exe"))
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
        string[] sys;
        try { sys = Directory.Exists(AppPaths.Bin) ? Directory.GetFiles(AppPaths.Bin, "*.sys") : Array.Empty<string>(); }
        catch { sys = Array.Empty<string>(); }

        if (sys.Length > 0)
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Драйвер найден: " + string.Join(", ", sys.Select(Path.GetFileName));
        }
        else
        {
            r.Status = CheckStatus.Failed;
            r.Detail = "Файл WinDivert64.sys не найден в папке bin. Скорее всего его удалил антивирус — восстановите файл и добавьте папку в исключения.";
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
    private static void CheckSecureDns(CheckResult r)
    {
        var found = false;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters");
            if (root != null) found = ScanDohFlags(root, 0);
        }
        catch { }

        if (found)
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "В системе настроен зашифрованный DNS (DoH).";
        }
        else
        {
            r.Status = CheckStatus.Warning;
            r.Detail = "Зашифрованный DNS не обнаружен. Настройте DNS-over-HTTPS в браузере со сторонним DNS-провайдером; " +
                       "в Windows 11 это можно включить в параметрах адаптера, тогда предупреждение исчезнет.";
        }
    }

    private static bool ScanDohFlags(RegistryKey key, int depth)
    {
        if (depth > 8) return false;

        foreach (var valueName in key.GetValueNames())
        {
            if (!string.Equals(valueName, "DohFlags", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var raw = key.GetValue(valueName);
                if (raw != null && Convert.ToInt64(raw) > 0) return true;
            }
            catch { }
        }

        foreach (var subName in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub != null && ScanDohFlags(sub, depth + 1)) return true;
            }
            catch { }
        }

        return false;
    }

    // ── 12. hosts ─────────────────────────────────────────────────────────────
    private static void CheckHosts(CheckResult r)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\drivers\etc\hosts");

        if (!File.Exists(path))
        {
            r.Status = CheckStatus.Ok;
            r.Detail = "Файл hosts не найден — проверять нечего.";
            return;
        }

        var hits = new List<string>();
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                    hits.Add(line);
            }
        }
        catch (Exception ex)
        {
            r.Status = CheckStatus.Warning;
            r.Detail = "Не удалось прочитать файл hosts: " + ex.Message;
            return;
        }

        if (hits.Count > 0)
        {
            r.Status = CheckStatus.Warning;
            r.Detail = $"В файле hosts есть записи для youtube.com или youtu.be ({hits.Count} шт.). " +
                       "Это может мешать доступу к YouTube. Первая запись: " + Shorten(hits[0], 120);
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
        var winwsRunning = ProcessUtil.IsProcessRunning("winws.exe");
        var divert = await ProcessUtil.ScAsync("query \"WinDivert\"", ct).ConfigureAwait(false);
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
        r.Fix = async () =>
        {
            await ProcessUtil.ShellAsync("net stop \"WinDivert\"").ConfigureAwait(false);
            await ProcessUtil.ScAsync("delete \"WinDivert\"").ConfigureAwait(false);

            var after = await ProcessUtil.ScAsync("query \"WinDivert\"").ConfigureAwait(false);
            if (!after.Success || after.All.Contains("1060"))
                return "Служба WinDivert успешно удалена.";

            // Не удалилась — виноват другой обход, использующий тот же драйвер
            var removed = await RemoveConflictingAsync(await FindConflictingAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            await ProcessUtil.ShellAsync("net stop \"WinDivert\"").ConfigureAwait(false);
            await ProcessUtil.ScAsync("delete \"WinDivert\"").ConfigureAwait(false);
            var final = await ProcessUtil.ScAsync("query \"WinDivert\"").ConfigureAwait(false);

            if (!final.Success || final.All.Contains("1060"))
                return removed.Count > 0
                    ? "Служба WinDivert удалена после удаления конфликтующих служб (" + string.Join(", ", removed) + ")."
                    : "Служба WinDivert успешно удалена.";

            return removed.Count > 0
                ? "WinDivert всё ещё не удаляется. Удалены конфликтующие службы: " + string.Join(", ", removed) +
                  ". Проверьте вручную, какой ещё обход блокировок использует WinDivert."
                : "Конфликтующих служб не найдено, но WinDivert не удаляется. Проверьте вручную, какой обход блокировок его использует.";
        };
    }

    // ── 14. Конфликтующие обходы ──────────────────────────────────────────────
    private static async Task CheckConflictsAsync(CheckResult r, CancellationToken ct)
    {
        var found = await FindConflictingAsync(ct).ConfigureAwait(false);

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
        r.Fix = async () =>
        {
            var again = await FindConflictingAsync(CancellationToken.None).ConfigureAwait(false);
            if (again.Count == 0) return "Конфликтующих служб больше нет.";

            var removed = await RemoveConflictingAsync(again).ConfigureAwait(false);

            foreach (var name in new[] { "WinDivert", "WinDivert14" })
            {
                await ProcessUtil.ShellAsync($"net stop \"{name}\"").ConfigureAwait(false);
                await ProcessUtil.ScAsync($"delete \"{name}\"").ConfigureAwait(false);
            }

            var left = again.Except(removed, StringComparer.OrdinalIgnoreCase).ToList();
            if (left.Count == 0)
                return "Удалены службы: " + string.Join(", ", removed) + ". Драйверы WinDivert также очищены.";

            return "Удалены службы: " + (removed.Count > 0 ? string.Join(", ", removed) : "нет") +
                   ". Не удалось удалить: " + string.Join(", ", left) + ".";
        };
    }

    private static async Task<List<string>> FindConflictingAsync(CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var name in ConflictingServices)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var res = await ProcessUtil.ScAsync($"query \"{name}\"", ct).ConfigureAwait(false);
                if (res.Success && !res.All.Contains("1060")) found.Add(name);
            }
            catch { }
        }
        return found;
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
                if (!check.Success || check.All.Contains("1060")) removed.Add(name);
            }
            catch { }
        }
        return removed;
    }

    // ── Вспомогательное ───────────────────────────────────────────────────────
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

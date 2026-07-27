using System.Security.Cryptography;
using System.Text;

namespace ZapretGui.Core;

public enum IpsetMode { Loaded, None, Any, Unknown }

/// <summary>
/// Переключатели-«флаги» из service.bat: game_filter, ipset, автопроверка обновлений,
/// подмена активных fake-пакетов и TCP timestamps. Ни один метод не бросает исключений.
/// </summary>
public static class FeatureFlags
{
    private const string GameFlagFileName = "game_filter.enabled";
    private const string CheckUpdatesFlagFileName = "check_updates.enabled";
    private const string IpsetFileName = "ipset-all.txt";
    private const string IpsetBackupFileName = "ipset-all.txt.backup";
    private const string IpsetSentinel = "203.0.113.113/32";

    private static string GameFlagFile => AppPaths.Utils + GameFlagFileName;
    private static string CheckUpdatesFlagFile => AppPaths.Utils + CheckUpdatesFlagFileName;
    private static string IpsetFile => AppPaths.Lists + IpsetFileName;
    private static string IpsetBackupFile => AppPaths.Lists + IpsetBackupFileName;

    // ===== GAME FILTER =====================================================

    public static GameFilterMode GetGameFilter()
    {
        try
        {
            if (!File.Exists(GameFlagFile))
                return GameFilterMode.Disabled;

            // service.bat берёт только первую непустую строку файла
            string? first = null;
            foreach (var line in File.ReadLines(GameFlagFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                first = line.Trim();
                break;
            }

            if (string.Equals(first, "all", StringComparison.OrdinalIgnoreCase)) return GameFilterMode.All;
            if (string.Equals(first, "tcp", StringComparison.OrdinalIgnoreCase)) return GameFilterMode.Tcp;
            return GameFilterMode.Udp;   // любое другое содержимое = udp
        }
        catch
        {
            return GameFilterMode.Disabled;
        }
    }

    public static void SetGameFilter(GameFilterMode m)
    {
        try
        {
            if (m == GameFilterMode.Disabled)
            {
                if (File.Exists(GameFlagFile))
                    File.Delete(GameFlagFile);
                return;
            }

            Directory.CreateDirectory(AppPaths.Utils);
            var value = m switch
            {
                GameFilterMode.All => "all",
                GameFilterMode.Tcp => "tcp",
                _ => "udp"
            };
            File.WriteAllText(GameFlagFile, value + "\r\n", new UTF8Encoding(false));
        }
        catch
        {
            // молча: переключатель не критичен
        }
    }

    // ===== IPSET ===========================================================

    public static IpsetMode GetIpsetMode()
    {
        try
        {
            if (!File.Exists(IpsetFile))
                return IpsetMode.Unknown;

            var text = File.ReadAllText(IpsetFile);
            if (CountLines(text) == 0)
                return IpsetMode.Any;

            return text.Contains(IpsetSentinel, StringComparison.Ordinal)
                ? IpsetMode.None
                : IpsetMode.Loaded;
        }
        catch
        {
            return IpsetMode.Unknown;
        }
    }

    /// <summary>
    /// Переводит ipset-all.txt в заданный режим, повторяя переименования из :ipset_switch.
    /// Реальный список живёт либо в ipset-all.txt (Loaded), либо в ipset-all.txt.backup.
    /// </summary>
    public static Task SetIpsetModeAsync(IpsetMode m) => Task.Run(() =>
    {
        try
        {
            if (m == IpsetMode.Unknown) return;

            var current = GetIpsetMode();
            if (current == m) return;

            Directory.CreateDirectory(AppPaths.Lists);

            switch (m)
            {
                case IpsetMode.None:
                    // Реальный список прячем в .backup — но только если он сейчас в ipset-all.txt,
                    // иначе затрём настоящий бэкап пустышкой.
                    if (current == IpsetMode.Loaded)
                    {
                        if (File.Exists(IpsetBackupFile))
                            File.Delete(IpsetBackupFile);
                        File.Move(IpsetFile, IpsetBackupFile);
                    }
                    File.WriteAllText(IpsetFile, IpsetSentinel + "\r\n", new UTF8Encoding(false));
                    break;

                case IpsetMode.Any:
                    // Пустой файл = фильтровать любой IP. Бэкап не трогаем.
                    if (current == IpsetMode.Loaded)
                    {
                        if (File.Exists(IpsetBackupFile))
                            File.Delete(IpsetBackupFile);
                        File.Move(IpsetFile, IpsetBackupFile);
                    }
                    File.WriteAllText(IpsetFile, string.Empty, new UTF8Encoding(false));
                    break;

                case IpsetMode.Loaded:
                    if (!File.Exists(IpsetBackupFile))
                        return;   // восстанавливать нечего — сначала «Обновить ipset»
                    if (File.Exists(IpsetFile))
                        File.Delete(IpsetFile);
                    File.Move(IpsetBackupFile, IpsetFile);
                    break;
            }
        }
        catch
        {
            // молча
        }
    });

    // ===== AUTO UPDATE CHECK ===============================================

    public static bool GetAutoUpdateCheck()
    {
        try { return File.Exists(CheckUpdatesFlagFile); }
        catch { return false; }
    }

    public static void SetAutoUpdateCheck(bool on)
    {
        try
        {
            if (on)
            {
                Directory.CreateDirectory(AppPaths.Utils);
                if (!File.Exists(CheckUpdatesFlagFile))
                    File.WriteAllText(CheckUpdatesFlagFile, "ENABLED \r\n", new UTF8Encoding(false));
            }
            else if (File.Exists(CheckUpdatesFlagFile))
            {
                File.Delete(CheckUpdatesFlagFile);
            }
        }
        catch
        {
            // молча
        }
    }

    // ===== TCP TIMESTAMPS ==================================================

    public static async Task<bool> TcpTimestampsEnabledAsync()
    {
        try
        {
            var r = await ProcessUtil.ShellAsync("netsh interface tcp show global", 15000).ConfigureAwait(false);
            foreach (var raw in r.All.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                // netsh локализован, поэтому проверяем и английский, и русский варианты
                bool isTimestampRow =
                    line.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("метк", StringComparison.OrdinalIgnoreCase);
                if (!isTimestampRow) continue;

                if (line.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("включ", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void EnableTcpTimestamps()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (await TcpTimestampsEnabledAsync().ConfigureAwait(false)) return;
                await ProcessUtil.ShellAsync("netsh interface tcp set global timestamps=enabled", 15000)
                    .ConfigureAwait(false);
            }
            catch
            {
                // молча
            }
        });
    }

    // ===== ACTIVE FAKES ====================================================

    public static List<string> ListFakeFiles()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(AppPaths.Bin)) return result;

            foreach (var path in Directory.EnumerateFiles(AppPaths.Bin, "*.bin", SearchOption.TopDirectoryOnly))
            {
                var baseName = Path.GetFileNameWithoutExtension(path);
                if (baseName.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(baseName);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // молча
        }
        return result;
    }

    public static string CurrentActiveFake(string activeFileName)
    {
        const string unknown = "—";
        try
        {
            if (string.IsNullOrWhiteSpace(activeFileName)) return unknown;

            var activePath = Path.Combine(AppPaths.Bin, activeFileName);
            var activeHash = HashOf(activePath);
            if (activeHash is null) return unknown;

            foreach (var baseName in ListFakeFiles())
            {
                var hash = HashOf(Path.Combine(AppPaths.Bin, baseName + ".bin"));
                if (hash is not null && string.Equals(hash, activeHash, StringComparison.OrdinalIgnoreCase))
                    return baseName;
            }
            return unknown;
        }
        catch
        {
            return unknown;
        }
    }

    public static bool ReplaceActiveFake(string activeFileName, string fakeBaseName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(activeFileName) || string.IsNullOrWhiteSpace(fakeBaseName))
                return false;

            var source = Path.Combine(AppPaths.Bin, fakeBaseName + ".bin");
            if (!File.Exists(source)) return false;

            var target = Path.Combine(AppPaths.Bin, activeFileName);
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return false;

            if (File.Exists(target)) File.Delete(target);
            File.Copy(source, target, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ===== helpers =========================================================

    private static string? HashOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Считает строки так же, как `find /c /v ""` в cmd: пустой файл = 0.</summary>
    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var count = 0;
        foreach (var ch in text)
            if (ch == '\n') count++;
        if (!text.EndsWith('\n')) count++;
        return count;
    }
}

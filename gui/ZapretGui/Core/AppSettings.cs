using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace ZapretGui.Core;

/// <summary>Пользовательские настройки приложения (%APPDATA%\ZapretGUI\settings.json).</summary>
public sealed class AppSettings
{
    public string? LastStrategy { get; set; }
    public bool AutoStartBypass { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool CheckUpdatesOnLaunch { get; set; } = true;
    public string Accent { get; set; } = "Violet";
    public bool ReducedMotion { get; set; }

    /// <summary>Поднимать обход заново, если winws.exe упал сам (до трёх попыток за 10 минут).</summary>
    public bool AutoRestartOnCrash { get; set; } = true;

    private static readonly object Sync = new();
    private static AppSettings? _current;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Current
    {
        get
        {
            lock (Sync)
            {
                _current ??= Load();
                return _current;
            }
        }
    }

    private static AppSettings Load()
    {
        try
        {
            var path = AppPaths.SettingsFile;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.Accent))
                            loaded.Accent = "Violet";
                        return loaded;
                    }
                }
            }
        }
        catch
        {
            // повреждённый или недоступный файл — молча откатываемся к значениям по умолчанию
        }

        return new AppSettings();
    }

    /// <summary>Сохраняет текущие настройки. Никогда не бросает исключений.</summary>
    public static void Save()
    {
        AppSettings snapshot;
        lock (Sync)
        {
            _current ??= Load();
            snapshot = _current;
        }

        try
        {
            var path = AppPaths.SettingsFile;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);

            // запись во временный файл + замена, чтобы не потерять настройки при сбое
            if (File.Exists(path))
                File.Replace(tmp, path, null, true);
            else
                File.Move(tmp, path);
        }
        catch
        {
            try
            {
                var fallback = AppPaths.SettingsFile;
                File.WriteAllText(fallback, JsonSerializer.Serialize(snapshot, JsonOptions));
            }
            catch
            {
                // ignore
            }
        }
    }
}

/// <summary>Автозапуск через HKCU\...\Run.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZapretGUI";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null)
                return;

            if (!on)
            {
                key.DeleteValue(ValueName, false);
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;

            key.SetValue(ValueName, "\"" + exe + "\" --minimized", RegistryValueKind.String);
        }
        catch
        {
            // недостаточно прав или политика блокирует Run — просто игнорируем
        }
    }
}

/// <summary>Проверка и повышение прав администратора.</summary>
public static class Elevation
{
    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Перезапускает процесс с правами администратора. false — пользователь отменил UAC.</summary>
    public static bool RelaunchAsAdmin(string? extraArgs = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppPaths.Root,
            };

            if (!string.IsNullOrWhiteSpace(extraArgs))
                psi.Arguments = extraArgs;

            var p = Process.Start(psi);
            return p is not null;
        }
        catch
        {
            // ERROR_CANCELLED (1223) при отказе в UAC и любые другие сбои
            return false;
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace ZapretGui.Core;

/// <summary>Состояние сторонней утилиты TgWsProxy.</summary>
public enum TgProxyState
{
    /// <summary>Исполняемый файл не найден и процесс не запущен.</summary>
    NotInstalled,

    /// <summary>Файл найден, процесс не запущен.</summary>
    Stopped,

    /// <summary>Процесс работает.</summary>
    Running
}

/// <summary>
/// Обёртка над TgWsProxy (Flowseal) — локальным MTProto-прокси для Telegram Desktop.
/// Бинарник сторонний и в поставку не входит: оболочка только ищет его, запускает,
/// останавливает и собирает tg://-ссылку для настройки клиента.
/// Собственные настройки лежат в telegram.json рядом с settings.json — файл
/// AppSettings принадлежит другой части приложения и здесь не трогается.
/// Ни один публичный метод не бросает исключений.
/// </summary>
public sealed class TelegramProxy : ObservableObject
{
    /// <summary>Страница релизов утилиты.</summary>
    public const string ReleasesUrl = "https://github.com/Flowseal/tg-ws-proxy/releases/latest";

    private const string ExeMask = "TgWsProxy*.exe";
    private const string ProcessPrefix = "TgWsProxy";
    private const string NestedFolder = "tg-ws-proxy";
    private const string SettingsFileName = "telegram.json";

    private const int StartPollAttempts = 20;
    private const int StartPollDelayMs = 150;
    private const int KillWaitMs = 3000;

    private static readonly Lazy<TelegramProxy> LazyInstance =
        new(() => new TelegramProxy(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static TelegramProxy Instance => LazyInstance.Value;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);   // сериализует Start/Stop целиком

    // сохраняемое
    private string? _savedPath;
    private string? _secret;
    private bool _autoStart;

    // вычисляемое
    private TgProxyState _state = TgProxyState.NotInstalled;
    private string? _exe;
    private int _pid;

    private TelegramProxy()
    {
        LoadSettings();

        // Перебор процессов заметен в бюджете кадра — первичный поиск уходит в пул.
        try { _ = Task.Run(Refresh); }
        catch { /* пул недоступен только при выгрузке домена */ }
    }

    // ---------------------------------------------------------------- состояние

    public TgProxyState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>«не найден» / «остановлен» / «работает».</summary>
    public string StateText => State switch
    {
        TgProxyState.Running => "работает",
        TgProxyState.Stopped => "остановлен",
        _ => "не найден"
    };

    /// <summary>Заголовок карточки состояния — человеческая формулировка.</summary>
    public string StateTitle => State switch
    {
        TgProxyState.Running => "Прокси работает",
        TgProxyState.Stopped => "Прокси остановлен",
        _ => "TgWsProxy не найден"
    };

    /// <summary>Полный путь к найденному исполняемому файлу либо null.</summary>
    public string? ExecutablePath
    {
        get { lock (_gate) return _exe; }
    }

    /// <summary>Путь для показа в интерфейсе.</summary>
    public string ExecutablePathText => ExecutablePath
        // Процесс может работать, а путь быть недоступен (запущен от другого пользователя):
        // писать в этом случае «файл не найден» — прямое враньё.
        ?? (IsRunning ? "путь не определён, но процесс запущен" : "файл не найден");

    /// <summary>Папка с найденным файлом; если файла нет — корень zapret.</summary>
    public string ExecutableFolder
    {
        get
        {
            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe))
                return AppPaths.Root;
            try { return Path.GetDirectoryName(exe) ?? AppPaths.Root; }
            catch { return AppPaths.Root; }
        }
    }

    /// <summary>Папка, куда пользователю предлагается положить файл.</summary>
    public static string SuggestedFolder => AppPaths.Root;

    public bool IsFound => ExecutablePath is not null;

    public bool IsRunning => State == TgProxyState.Running;

    public bool CanStart => IsFound && !IsRunning;

    public bool CanStop => IsRunning;

    /// <summary>PID работающей утилиты; 0 — не запущена.</summary>
    public int ProcessId
    {
        get { lock (_gate) return _pid; }
    }

    public string ProcessIdText
    {
        get
        {
            var pid = ProcessId;
            return pid == 0 ? "—" : pid.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- адрес

    public string Host => "127.0.0.1";

    public int Port => 1443;

    public string PortText => Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Секрет MTProto из окна TgWsProxy. Принимает и целиком вставленную
    /// tg://proxy-ссылку — из неё берётся только параметр secret.
    /// </summary>
    public string? Secret
    {
        get { lock (_gate) return _secret; }
        set
        {
            var normalized = NormalizeSecret(value);
            lock (_gate)
            {
                if (string.Equals(_secret, normalized, StringComparison.Ordinal))
                    return;
                _secret = normalized;
            }

            SaveSettings();
            Post(() => RaiseMany(
                nameof(Secret), nameof(HasSecret), nameof(TelegramLink), nameof(CanConfigureTelegram)));
        }
    }

    public bool HasSecret => !string.IsNullOrWhiteSpace(Secret);

    public bool CanConfigureTelegram => HasSecret;

    /// <summary>tg://proxy?server=..&amp;port=..&amp;secret=.. либо null, пока секрет не задан.</summary>
    public string? TelegramLink
    {
        get
        {
            var secret = Secret;
            if (string.IsNullOrWhiteSpace(secret))
                return null;

            try
            {
                return "tg://proxy?server=" + Host +
                       "&port=" + PortText +
                       "&secret=" + Uri.EscapeDataString(secret);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Поднимать прокси вместе с обходом DPI.</summary>
    public bool AutoStartWithBypass
    {
        get { lock (_gate) return _autoStart; }
        set
        {
            lock (_gate)
            {
                if (_autoStart == value)
                    return;
                _autoStart = value;
            }

            SaveSettings();
            Post(() => Raise(nameof(AutoStartWithBypass)));
        }
    }

    public event EventHandler? StateChanged;

    // ---------------------------------------------------------------- операции

    /// <summary>Перечитывает расположение файла и наличие процесса.</summary>
    public void Refresh()
    {
        string? saved;
        lock (_gate) saved = _savedPath;

        string? exe;
        try { exe = FindExecutable(saved); }
        catch { exe = null; }

        int pid;
        try { pid = FindProcessId(); }
        catch { pid = 0; }

        // Утилиту часто ставят в автозагрузку, а не рядом с оболочкой. Если процесс уже работает,
        // путь берём прямо у него — так находится любая установка, где бы она ни лежала.
        if (exe is null && pid != 0)
        {
            try { exe = GetProcessPath(pid); }
            catch { /* MainModule чужого процесса может быть недоступен */ }
        }

        var state = pid != 0
            ? TgProxyState.Running
            : exe is not null ? TgProxyState.Stopped : TgProxyState.NotInstalled;

        bool changed;
        lock (_gate)
        {
            changed = _state != state
                      || _pid != pid
                      || !string.Equals(_exe, exe, StringComparison.OrdinalIgnoreCase);
            _state = state;
            _pid = pid;
            _exe = exe;
        }

        if (!changed)
            return;

        Post(NotifyState);
    }

    /// <summary>Запускает утилиту. false — файл не найден либо процесс не поднялся.</summary>
    public async Task<bool> StartAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            Refresh();
            if (State == TgProxyState.Running)
                return true;

            var exe = ExecutablePath;
            if (exe is null)
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = SafeDirectory(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Утилита самостоятельная и живёт в трее — дескриптор нам не нужен.
                using var started = Process.Start(psi);
                if (started is null)
                    return false;
            }
            catch
            {
                return false;
            }

            // трей и слушающий сокет поднимаются не мгновенно
            for (var i = 0; i < StartPollAttempts; i++)
            {
                await Task.Delay(StartPollDelayMs).ConfigureAwait(false);
                Refresh();
                if (State == TgProxyState.Running)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Завершает утилиту, даже если её запускали не мы.</summary>
    public async Task StopAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(KillAll).ConfigureAwait(false);
            Refresh();
        }
        catch
        {
            // KillAll и Refresh молчаливые, но страхуемся
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Запоминает путь, выбранный пользователем. false — файла нет либо это не exe.</summary>
    public bool SetExecutablePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var full = Path.GetFullPath(path.Trim().Trim('"'));
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return false;

            lock (_gate) _savedPath = full;
            SaveSettings();
            Refresh();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Забывает сохранённый путь и ищет файл заново.</summary>
    public void ClearExecutablePath()
    {
        lock (_gate) _savedPath = null;
        SaveSettings();
        Refresh();
    }

    public void OpenReleasesPage() => AppState.OpenExternal(ReleasesUrl);

    /// <summary>Открывает tg://-ссылку. Без секрета не делает ничего.</summary>
    public void OpenTelegramLink()
    {
        var link = TelegramLink;
        if (string.IsNullOrEmpty(link))
            return;
        AppState.OpenExternal(link);
    }

    /// <summary>Открывает папку с файлом либо ту, куда его следует положить.</summary>
    public void OpenFolder() => AppState.OpenExternal(ExecutableFolder);

    /// <summary>
    /// Вызывается после старта обхода: поднимает прокси, если пользователь этого просил.
    /// Возвращает false, когда переключатель выключен или запуск не удался.
    /// </summary>
    public Task<bool> StartWithBypassAsync()
        => AutoStartWithBypass && !IsRunning ? StartAsync() : Task.FromResult(false);

    // ---------------------------------------------------------------- поиск

    private static string? FindExecutable(string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved))
        {
            try
            {
                if (File.Exists(saved))
                    return saved;
            }
            catch
            {
                // сохранённый путь мог уехать на отключённый диск
            }
        }

        foreach (var dir in CandidateDirectories())
        {
            var found = BestInDirectory(dir);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in RootCandidates())
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            if (seen.Add(root))
                yield return root;

            string nested;
            try { nested = Path.Combine(root, NestedFolder); }
            catch { continue; }

            if (seen.Add(nested))
                yield return nested;
        }
    }

    private static IEnumerable<string> RootCandidates()
    {
        yield return AppPaths.Root;

        string? processDir = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
                processDir = Path.GetDirectoryName(exe);
        }
        catch
        {
            processDir = null;
        }

        if (!string.IsNullOrWhiteSpace(processDir))
            yield return processDir;

        string? baseDir = null;
        try { baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/'); }
        catch { baseDir = null; }

        if (!string.IsNullOrWhiteSpace(baseDir))
            yield return baseDir;

        // Обычные места, куда утилиту кладут руками: автозагрузка, «Загрузки», рабочий стол.
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Startup,
                     Environment.SpecialFolder.CommonStartup,
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.UserProfile,
                 })
        {
            string? path = null;
            try { path = Environment.GetFolderPath(folder); }
            catch { /* недоступная папка профиля */ }

            if (!string.IsNullOrWhiteSpace(path))
                yield return path;
        }

        string? downloads = null;
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
                downloads = Path.Combine(profile, "Downloads");
        }
        catch { /* ignore */ }

        if (!string.IsNullOrWhiteSpace(downloads))
            yield return downloads;
    }

    private static string? BestInDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return null;

            string? best = null;
            var bestRank = int.MaxValue;

            foreach (var file in Directory.EnumerateFiles(dir, ExeMask, SearchOption.TopDirectoryOnly))
            {
                var rank = Rank(Path.GetFileName(file) ?? string.Empty);
                if (rank >= bestRank)
                    continue;
                bestRank = rank;
                best = file;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Меньше — предпочтительнее. Сборки под другую архитектуру уходят в хвост.</summary>
    private static int Rank(string fileName)
    {
        var arm = false;
        try { arm = RuntimeInformation.OSArchitecture == Architecture.Arm64; }
        catch { /* при недоступности считаем, что не ARM */ }

        if (fileName.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            return arm ? 1 : 40;

        // сборки для Windows 7 работают и на новых системах, но берём их последними
        if (fileName.Contains("_7_", StringComparison.OrdinalIgnoreCase))
            return 30;

        if (fileName.Equals("TgWsProxy_windows.exe", StringComparison.OrdinalIgnoreCase))
            return arm ? 12 : 10;

        if (fileName.Equals("TgWsProxy.exe", StringComparison.OrdinalIgnoreCase))
            return arm ? 13 : 11;

        return 20;
    }

    private static string? GetProcessPath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var path = p.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static int FindProcessId()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return 0; }

        var pid = 0;
        foreach (var p in all)
        {
            try
            {
                if (pid == 0 && p.ProcessName.StartsWith(ProcessPrefix, StringComparison.OrdinalIgnoreCase))
                    pid = p.Id;
            }
            catch
            {
                // процесс мог исчезнуть между снимком и обращением
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        return pid;
    }

    private static void KillAll()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return; }

        foreach (var p in all)
        {
            var match = false;
            try { match = p.ProcessName.StartsWith(ProcessPrefix, StringComparison.OrdinalIgnoreCase); }
            catch { match = false; }

            try
            {
                if (match && !p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(KillWaitMs);
                }
            }
            catch
            {
                // процесс завершился сам либо не хватает прав
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private static string SafeDirectory(string exe)
    {
        try { return Path.GetDirectoryName(exe) ?? AppPaths.Root; }
        catch { return AppPaths.Root; }
    }

    // ---------------------------------------------------------------- секрет

    /// <summary>Принимает и голый секрет, и целую ссылку tg://proxy?...&amp;secret=...</summary>
    private static string? NormalizeSecret(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim().Trim('"', '\'');

        var marker = value.IndexOf("secret=", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            value = value[(marker + "secret=".Length)..];

            var end = value.IndexOfAny(new[] { '&', ' ', '\r', '\n', '\t' });
            if (end >= 0)
                value = value[..end];

            try { value = Uri.UnescapeDataString(value); }
            catch { /* оставляем как есть */ }
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    // ---------------------------------------------------------------- файл настроек

    private static string SettingsPath
    {
        get
        {
            try { return Path.Combine(AppPaths.DataDir, SettingsFileName); }
            catch { return SettingsFileName; }
        }
    }

    private void LoadSettings()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return;

            // читаем документом, а не десериализацией в тип: полей три и они простые
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            lock (_gate)
            {
                _savedPath = ReadString(root, "ExecutablePath");
                _secret = NormalizeSecret(ReadString(root, "Secret"));
                _autoStart = ReadBool(root, "AutoStartWithBypass");
            }
        }
        catch
        {
            // повреждённый или недоступный файл — работаем со значениями по умолчанию
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private void SaveSettings()
    {
        string? exePath, secret;
        bool autoStart;
        lock (_gate)
        {
            exePath = _savedPath;
            secret = _secret;
            autoStart = _autoStart;
        }

        try
        {
            var path = SettingsPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                if (exePath is null) writer.WriteNull("ExecutablePath");
                else writer.WriteString("ExecutablePath", exePath);
                if (secret is null) writer.WriteNull("Secret");
                else writer.WriteString("Secret", secret);
                writer.WriteBoolean("AutoStartWithBypass", autoStart);
                writer.WriteEndObject();
            }

            File.WriteAllBytes(path, buffer.ToArray());
        }
        catch
        {
            // диск занят или права отозваны — настройка просто не переживёт перезапуск
        }
    }

    // ---------------------------------------------------------------- уведомления

    private void NotifyState()
    {
        RaiseMany(
            nameof(State), nameof(StateText), nameof(StateTitle),
            nameof(ExecutablePath), nameof(ExecutablePathText), nameof(ExecutableFolder),
            nameof(IsFound), nameof(IsRunning), nameof(CanStart), nameof(CanStop),
            nameof(ProcessId), nameof(ProcessIdText));

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refresh вызывается и из фоновых задач — уведомления уводим в поток интерфейса.</summary>
    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            try { action(); }
            catch { /* обработчики страницы не должны ронять ядро */ }
            return;
        }

        try { dispatcher.BeginInvoke(action); }
        catch { /* приложение уже закрывается */ }
    }
}

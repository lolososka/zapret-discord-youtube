using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace ZapretGui.Core;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// Единая точка состояния приложения: все страницы биндятся сюда.
/// Живёт столько же, сколько процесс; создаётся на UI-потоке.
/// </summary>
public sealed class AppState : ObservableObject
{
    private static AppState? _instance;
    public static AppState Instance => _instance ??= new AppState();

    private readonly DispatcherTimer _ticker;
    private readonly BypassController _bypass = BypassController.Instance;
    private readonly StrategyPreferences _prefs = StrategyPreferences.Load();
    private int _slowTick;

    // «Работала у вас» ставится один раз за запуск — иначе тост повторялся бы каждую секунду.
    private string? _markedThisRun;
    private bool _prefsDirty;

    private AppState()
    {
        Strategies = new ObservableCollection<Strategy>();
        Log = new ObservableCollection<LogLine>();
        Diagnostics = new ObservableCollection<CheckResult>();
        Probes = new ObservableCollection<ProbeResult>();

        ToggleBypassCommand = new AsyncRelayCommand(ToggleBypassAsync, () => SelectedStrategy is not null);
        InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync, () => SelectedStrategy is not null);
        RemoveServiceCommand = new AsyncRelayCommand(RemoveServiceAsync);
        RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsAsync);
        RunProbesCommand = new AsyncRelayCommand(RunProbesAsync);
        UpdateIpsetCommand = new AsyncRelayCommand(UpdateIpsetAsync);
        CheckHostsCommand = new AsyncRelayCommand(CheckHostsAsync);
        CheckUpdatesCommand = new AsyncRelayCommand(() => CheckUpdatesAsync(silent: false));
        ClearLogCommand = new RelayCommand(() => { Log.Clear(); Raise(nameof(HasLog)); });
        OpenFolderCommand = new RelayCommand(() => OpenExternal(AppPaths.Root));

        AutoPickCommand = new AsyncRelayCommand(AutoPickAsync, () => !Tester.IsRunning && Strategies.Count > 0);
        CancelAutoPickCommand = new RelayCommand(() => Tester.Cancel(), () => Tester.IsRunning);

        Tester.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(StrategyTester.IsRunning)) return;
            AutoPickCommand.RaiseCanExecuteChanged();
            CancelAutoPickCommand.RaiseCanExecuteChanged();
        };

        _bypass.StateChanged += (_, _) => OnBypassStateChanged();
        _bypass.LogWritten += (_, line) => AppendLog(line);

        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += OnTick;
    }

    // ---------- Коллекции ----------

    /// <summary>Живые метрики процесса и интерфейса — для плиток и графика на «Панели».</summary>
    public TrafficMonitor Traffic => TrafficMonitor.Instance;

    public ObservableCollection<Strategy> Strategies { get; }
    public ObservableCollection<LogLine> Log { get; }
    public ObservableCollection<CheckResult> Diagnostics { get; }
    public ObservableCollection<ProbeResult> Probes { get; }

    public bool HasLog => Log.Count > 0;

    // ---------- Выбор стратегии ----------

    private Strategy? _selectedStrategy;
    public Strategy? SelectedStrategy
    {
        get => _selectedStrategy;
        set
        {
            if (!Set(ref _selectedStrategy, value)) return;
            AppSettings.Current.LastStrategy = value?.Name;
            AppSettings.Save();
            RaiseMany(nameof(SelectedStrategyName), nameof(SelectedStrategySummary));
            ToggleBypassCommand.RaiseCanExecuteChanged();
            InstallServiceCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedStrategyName => SelectedStrategy?.DisplayName ?? "Стратегия не выбрана";
    public string SelectedStrategySummary => SelectedStrategy?.Summary ?? "Откройте «Стратегии» и выберите вариант обхода";

    // ---------- Состояние обхода ----------

    public BypassState BypassState => _bypass.State;
    public bool IsRunning => _bypass.State is BypassState.Running;
    public bool IsBusy => _bypass.State is BypassState.Starting or BypassState.Stopping;

    public string StatusTitle => _bypass.State switch
    {
        BypassState.Running => "Обход активен",
        BypassState.Starting => "Запуск…",
        BypassState.Stopping => "Остановка…",
        BypassState.Failed => "Не удалось запустить",
        _ => "Обход выключен",
    };

    public string StatusSubtitle => _bypass.State switch
    {
        BypassState.Running => _bypass.ActiveStrategy is null
            ? "winws.exe работает (запущен извне или как служба)"
            : $"{_bypass.ActiveStrategy.DisplayName} · {UptimeText}",
        BypassState.Starting => "Поднимаем WinDivert и фильтры",
        BypassState.Stopping => "Завершаем winws.exe",
        BypassState.Failed => "Откройте «Журнал» — там причина",
        _ => "Discord и YouTube идут напрямую",
    };

    public string UptimeText
    {
        get
        {
            var t = _bypass.Uptime;
            if (t <= TimeSpan.Zero) return "00:00";
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    // ---------- Служба и переключатели ----------

    private ServiceState _serviceState = ServiceState.NotInstalled;
    public ServiceState ServiceState
    {
        get => _serviceState;
        private set { if (Set(ref _serviceState, value)) RaiseMany(nameof(ServiceStateText), nameof(IsServiceInstalled)); }
    }

    public bool IsServiceInstalled => ServiceState != ServiceState.NotInstalled;

    public string ServiceStateText => ServiceState switch
    {
        ServiceState.Running => "Служба работает",
        ServiceState.Stopped => "Служба установлена, остановлена",
        ServiceState.Pending => "Служба меняет состояние…",
        ServiceState.Unknown => "Состояние службы неизвестно",
        _ => "Служба не установлена",
    };

    private string? _installedServiceStrategy;
    public string? InstalledServiceStrategy
    {
        get => _installedServiceStrategy;
        private set => Set(ref _installedServiceStrategy, value);
    }

    private GameFilterMode _gameFilter;
    public GameFilterMode GameFilter
    {
        get => _gameFilter;
        set
        {
            if (!Set(ref _gameFilter, value)) return;
            FeatureFlags.SetGameFilter(value);
            RaiseMany(nameof(GameFilterText), nameof(IsGameFilterOn));
            if (IsRunning) Notify("Игровой фильтр изменён — перезапустите обход", ToastKind.Warning);
        }
    }

    public bool IsGameFilterOn => GameFilter != GameFilterMode.Disabled;

    public string GameFilterText => GameFilter switch
    {
        GameFilterMode.All => "TCP и UDP",
        GameFilterMode.Tcp => "только TCP",
        GameFilterMode.Udp => "только UDP",
        _ => "выключен",
    };

    private IpsetMode _ipsetMode = IpsetMode.Unknown;
    public IpsetMode IpsetMode
    {
        get => _ipsetMode;
        private set { if (Set(ref _ipsetMode, value)) Raise(nameof(IpsetModeText)); }
    }

    public string IpsetModeText => IpsetMode switch
    {
        IpsetMode.Loaded => "список загружен",
        IpsetMode.None => "выключен",
        IpsetMode.Any => "любые адреса",
        _ => "неизвестно",
    };

    public async Task SetIpsetModeAsync(IpsetMode mode)
    {
        await FeatureFlags.SetIpsetModeAsync(mode);
        IpsetMode = FeatureFlags.GetIpsetMode();
        if (IsRunning) Notify("Режим IPSet изменён — перезапустите обход", ToastKind.Warning);
    }

    private bool _autoUpdateCheck;
    public bool AutoUpdateCheck
    {
        get => _autoUpdateCheck;
        set { if (Set(ref _autoUpdateCheck, value)) FeatureFlags.SetAutoUpdateCheck(value); }
    }

    // ---------- Обновления ----------

    private string? _updateAvailableVersion;
    public string? UpdateAvailableVersion
    {
        get => _updateAvailableVersion;
        private set { if (Set(ref _updateAvailableVersion, value)) Raise(nameof(HasUpdate)); }
    }

    public bool HasUpdate => !string.IsNullOrEmpty(UpdateAvailableVersion);
    public string LocalVersion => UpdateService.LocalVersion;

    // ---------- Прогресс длительных операций ----------

    private bool _isDiagnosticsRunning;
    public bool IsDiagnosticsRunning { get => _isDiagnosticsRunning; private set => Set(ref _isDiagnosticsRunning, value); }

    private bool _isProbing;
    public bool IsProbing { get => _isProbing; private set => Set(ref _isProbing, value); }

    private string? _busyMessage;
    public string? BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }

    // ---------- Команды ----------

    public AsyncRelayCommand ToggleBypassCommand { get; }
    public AsyncRelayCommand InstallServiceCommand { get; }
    public AsyncRelayCommand RemoveServiceCommand { get; }
    public AsyncRelayCommand RunDiagnosticsCommand { get; }
    public AsyncRelayCommand RunProbesCommand { get; }
    public AsyncRelayCommand UpdateIpsetCommand { get; }
    public AsyncRelayCommand CheckHostsCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Перебирает все стратегии и оставляет лучшую в Tester.Best.</summary>
    public AsyncRelayCommand AutoPickCommand { get; }
    public RelayCommand CancelAutoPickCommand { get; }

    // ---------- Избранное и «работала у вас» ----------

    public StrategyTester Tester => StrategyTester.Instance;

    /// <summary>Имя стратегии, которая последней проработала дольше минуты.</summary>
    public string? LastWorkingName => _prefs.LastWorking;

    public bool IsFavorite(Strategy? s)
        => s is not null && _prefs.Favorites.Contains(s.Name);

    public void ToggleFavorite(Strategy? s)
    {
        if (s is null) return;

        if (_prefs.Favorites.Contains(s.Name))
            _prefs.Favorites.Remove(s.Name);
        else
            _prefs.Favorites.Add(s.Name);

        s.IsFavorite = _prefs.Favorites.Contains(s.Name);
        _prefs.Save();
        StrategyMarksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Изменились звёздочки или отметка «работала у вас» — списку пора пересортироваться.</summary>
    public event EventHandler? StrategyMarksChanged;

    private void ApplyStrategyMarks()
    {
        foreach (var s in Strategies)
        {
            s.IsFavorite = _prefs.Favorites.Contains(s.Name);
            s.HasWorked = _prefs.SuccessSeconds.TryGetValue(s.Name, out var sec) && sec >= WorkedThresholdSeconds;
        }
    }

    private const int WorkedThresholdSeconds = 60;

    /// <summary>
    /// Стратегия, продержавшаяся минуту, — единственное честное свидетельство того,
    /// что она работает у этого провайдера. Статический флаг «рекомендуется» этого не знает.
    /// </summary>
    private void TrackWorkingStrategy()
    {
        var active = _bypass.ActiveStrategy;
        if (!IsRunning || active is null)
            return;

        int seconds = (int)_bypass.Uptime.TotalSeconds;
        if (seconds <= 0) return;

        if (!_prefs.SuccessSeconds.TryGetValue(active.Name, out var stored) || stored < seconds)
        {
            _prefs.SuccessSeconds[active.Name] = seconds;
            _prefsDirty = true;
        }

        if (seconds < WorkedThresholdSeconds) return;
        if (string.Equals(_markedThisRun, active.Name, StringComparison.OrdinalIgnoreCase)) return;

        _markedThisRun = active.Name;
        _prefs.LastWorking = active.Name;
        _prefs.Save();
        _prefsDirty = false;

        active.HasWorked = true;
        Raise(nameof(LastWorkingName));
        StrategyMarksChanged?.Invoke(this, EventArgs.Empty);
        Notify($"«{active.DisplayName}» работает больше минуты — отмечена как рабочая", ToastKind.Success);
    }

    private async Task AutoPickAsync()
    {
        if (Tester.IsRunning) return;

        if (ServiceState is ServiceState.Running)
        {
            Notify("Сначала удалите службу — она держит winws.exe", ToastKind.Warning);
            return;
        }

        if (Strategies.Count == 0)
        {
            Notify("Стратегии не найдены — проверьте папку zapret", ToastKind.Warning);
            return;
        }

        Notify("Перебор начат: обход будет перезапускаться на каждой стратегии", ToastKind.Info);

        await Tester.RunAsync(Strategies.ToList(), GameFilter, CancellationToken.None);

        var best = Tester.Best;
        if (best is null)
        {
            Notify("Ни одна стратегия не открыла сайты — загляните в «Диагностику»", ToastKind.Warning);
            return;
        }

        Notify($"Лучший результат: {best.Title} — {best.ScoreText}", ToastKind.Success);
    }

    /// <summary>Всплывающие уведомления: подписывается MainWindow.</summary>
    public event EventHandler<(string Message, ToastKind Kind)>? Notification;

    public void Notify(string message, ToastKind kind = ToastKind.Info)
        => Notification?.Invoke(this, (message, kind));

    // ---------- Жизненный цикл ----------

    public async Task InitializeAsync()
    {
        ReloadStrategies();

        GameFilter = FeatureFlags.GetGameFilter();
        IpsetMode = FeatureFlags.GetIpsetMode();
        _autoUpdateCheck = FeatureFlags.GetAutoUpdateCheck();
        Raise(nameof(AutoUpdateCheck));

        foreach (var line in _bypass.History) Log.Add(line);
        Raise(nameof(HasLog));

        _bypass.RefreshState();
        OnBypassStateChanged();
        _ticker.Start();
        Traffic.Start();

        await RefreshServiceStateAsync();

        if (AppSettings.Current.AutoStartBypass && !IsRunning && SelectedStrategy is not null)
            await ToggleBypassAsync();

        if (AppSettings.Current.CheckUpdatesOnLaunch)
            _ = CheckUpdatesAsync(silent: true);
    }

    public void ReloadStrategies()
    {
        var list = StrategyParser.LoadAll();
        Strategies.Clear();
        foreach (var s in list) Strategies.Add(s);

        ApplyStrategyMarks();

        // Приоритет подсказок: выбор пользователя → то, что у него уже работало → избранное → обычный старт.
        var wanted = AppSettings.Current.LastStrategy;
        var pick = list.FirstOrDefault(s => s.Name == wanted)
                   ?? list.FirstOrDefault(s => s.Name == _prefs.LastWorking)
                   ?? list.FirstOrDefault(s => s.IsFavorite)
                   ?? list.FirstOrDefault(s => s.IsRecommended)
                   ?? list.FirstOrDefault();

        _selectedStrategy = pick;
        RaiseMany(nameof(SelectedStrategy), nameof(SelectedStrategyName), nameof(SelectedStrategySummary));
        AutoPickCommand.RaiseCanExecuteChanged();
        ToggleBypassCommand.RaiseCanExecuteChanged();
        InstallServiceCommand.RaiseCanExecuteChanged();
    }

    public async Task ShutdownAsync()
    {
        _ticker.Stop();
        Traffic.Stop();
        Tester.Cancel();

        if (_prefsDirty)
        {
            _prefsDirty = false;
            _prefs.Save();
        }

        // Только свой процесс: обход, установленный как служба, должен пережить закрытие окна.
        await _bypass.StopAsync(ownedOnly: true);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (IsRunning)
        {
            RaiseMany(nameof(UptimeText), nameof(StatusSubtitle));
            TrackWorkingStrategy();
        }

        // Внешние изменения (служба, чужой winws.exe) проверяем реже.
        if (++_slowTick >= 5)
        {
            _slowTick = 0;
            _bypass.RefreshState();
            _ = RefreshServiceStateAsync();
        }
    }

    private void OnBypassStateChanged()
    {
        RaiseMany(nameof(BypassState), nameof(IsRunning), nameof(IsBusy),
                  nameof(StatusTitle), nameof(StatusSubtitle), nameof(UptimeText));
        ToggleBypassCommand.RaiseCanExecuteChanged();

        if (IsRunning) return;

        // Запуск закончился — сбрасываем метку и дописываем накопленное время на диск.
        _markedThisRun = null;
        if (!_prefsDirty) return;
        _prefsDirty = false;
        _prefs.Save();
    }

    private void AppendLog(LogLine line)
    {
        Log.Add(line);
        while (Log.Count > 2000) Log.RemoveAt(0);
        Raise(nameof(HasLog));
    }

    // ---------- Реализация команд ----------

    private async Task ToggleBypassAsync()
    {
        if (IsRunning)
        {
            if (ServiceState is ServiceState.Running && _bypass.ActiveStrategy is null)
            {
                Notify("winws.exe держит служба zapret — остановите её на вкладке «Стратегии»", ToastKind.Warning);
                return;
            }

            await _bypass.StopAsync();
            Notify("Обход остановлен", ToastKind.Info);
            return;
        }

        if (SelectedStrategy is null) return;

        if (ServiceState is ServiceState.Running)
        {
            Notify("Сначала удалите службу — она уже держит winws.exe", ToastKind.Warning);
            return;
        }

        var ok = await _bypass.StartAsync(SelectedStrategy, GameFilter);
        Notify(ok ? $"Обход запущен · {SelectedStrategy.DisplayName}" : "Запуск не удался — смотрите журнал",
               ok ? ToastKind.Success : ToastKind.Error);

        // Прокси Telegram поднимается следом, если пользователь включил это на странице «Телеграм».
        // Обратно вместе с обходом он не гасится: утилита самостоятельная и живёт в трее.
        if (ok) _ = TelegramProxy.Instance.StartWithBypassAsync();
    }

    private async Task InstallServiceAsync()
    {
        if (SelectedStrategy is null) return;
        BusyMessage = "Устанавливаем службу…";
        try
        {
            await _bypass.StopAsync();
            var r = await ZapretServiceManager.InstallAsync(SelectedStrategy, GameFilter);
            _bypass.Log(r.Output, r.Success ? LogLevel.Success : LogLevel.Error);
            Notify(r.Success ? "Служба установлена и запущена" : "Не удалось установить службу",
                   r.Success ? ToastKind.Success : ToastKind.Error);
            await RefreshServiceStateAsync();
            _bypass.RefreshState();
        }
        finally { BusyMessage = null; }
    }

    private async Task RemoveServiceAsync()
    {
        BusyMessage = "Удаляем службу…";
        try
        {
            var r = await ZapretServiceManager.RemoveAsync();
            _bypass.Log(r.Output, r.Success ? LogLevel.Success : LogLevel.Error);
            Notify(r.Success ? "Служба удалена" : "Не удалось удалить службу",
                   r.Success ? ToastKind.Success : ToastKind.Error);
            await RefreshServiceStateAsync();
            _bypass.RefreshState();
        }
        finally { BusyMessage = null; }
    }

    private async Task RefreshServiceStateAsync()
    {
        ServiceState = await ZapretServiceManager.QueryAsync();
        InstalledServiceStrategy = ServiceState == ServiceState.NotInstalled
            ? null
            : await ZapretServiceManager.InstalledStrategyNameAsync();
    }

    private async Task RunDiagnosticsAsync()
    {
        if (IsDiagnosticsRunning) return;
        IsDiagnosticsRunning = true;
        Diagnostics.Clear();
        try
        {
            var progress = new Progress<CheckResult>(r =>
            {
                var existing = Diagnostics.FirstOrDefault(x => x.Id == r.Id);
                if (existing is null) Diagnostics.Add(r);
                else
                {
                    var i = Diagnostics.IndexOf(existing);
                    Diagnostics[i] = r;
                }
            });
            await DiagnosticsRunner.RunAllAsync(progress, CancellationToken.None);

            var bad = Diagnostics.Count(d => d.Status == CheckStatus.Failed);
            var warn = Diagnostics.Count(d => d.Status == CheckStatus.Warning);
            var badText = $"{bad} {Plural(bad, "проблема", "проблемы", "проблем")}";
            var warnText = $"{warn} {Plural(warn, "предупреждение", "предупреждения", "предупреждений")}";

            Notify(bad > 0 ? $"Диагностика: {badText}, {warnText}"
                           : warn > 0 ? $"Диагностика: {warnText}"
                           : "Диагностика: всё в порядке",
                   bad > 0 ? ToastKind.Error : warn > 0 ? ToastKind.Warning : ToastKind.Success);
        }
        finally { IsDiagnosticsRunning = false; }
    }

    private async Task RunProbesAsync()
    {
        if (IsProbing) return;
        IsProbing = true;
        Probes.Clear();
        try
        {
            var tasks = ConnectivityTester.Sites.Select(async s =>
            {
                var r = await ConnectivityTester.ProbeAsync(s, CancellationToken.None);
                Probes.Add(r);
            });
            await Task.WhenAll(tasks);
        }
        finally { IsProbing = false; }
    }

    private async Task UpdateIpsetAsync()
    {
        BusyMessage = "Обновляем список IP…";
        try
        {
            var r = await UpdateService.UpdateIpsetAsync();
            IpsetMode = FeatureFlags.GetIpsetMode();
            Notify(r.Output, r.Success ? ToastKind.Success : ToastKind.Error);
        }
        finally { BusyMessage = null; }
    }

    private async Task CheckHostsAsync()
    {
        BusyMessage = "Проверяем файл hosts…";
        try
        {
            var r = await UpdateService.CheckHostsAsync();
            Notify(r.Output, r.Success ? ToastKind.Success : ToastKind.Warning);
        }
        finally { BusyMessage = null; }
    }

    private async Task CheckUpdatesAsync(bool silent)
    {
        var (ok, remote, newer) = await UpdateService.CheckAsync();
        if (!ok)
        {
            if (!silent) Notify("Не удалось проверить обновления", ToastKind.Warning);
            return;
        }

        UpdateAvailableVersion = newer ? remote : null;

        if (newer) Notify($"Доступна версия {remote}", ToastKind.Info);
        else if (!silent) Notify($"Установлена последняя версия {UpdateService.LocalVersion}", ToastKind.Success);
    }

    /// <summary>Русское склонение по числу: 1 проблема, 2 проблемы, 5 проблем.</summary>
    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    public static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }
    }
}

/// <summary>
/// Личный опыт пользователя со стратегиями: %APPDATA%\ZapretGUI\strategies.json.
/// Отдельно от settings.json — файл растёт по мере перебора и его не жалко потерять.
/// </summary>
public sealed class StrategyPreferences
{
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Имя стратегии, которая последней проработала дольше минуты.</summary>
    public string? LastWorking { get; set; }

    /// <summary>Имя стратегии → самое долгое непрерывное время работы, секунды.</summary>
    public Dictionary<string, int> SuccessSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string FilePath => Path.Combine(AppPaths.DataDir, "strategies.json");

    public static StrategyPreferences Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var loaded = JsonSerializer.Deserialize<StrategyPreferences>(json, JsonOptions);
                    if (loaded is not null)
                        return loaded.Normalized();
                }
            }
        }
        catch
        {
            // повреждённый файл — начинаем с чистого листа
        }

        return new StrategyPreferences();
    }

    /// <summary>Никогда не бросает: потеря звёздочек не повод падать.</summary>
    public void Save()
    {
        try
        {
            lock (Sync)
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            }
        }
        catch
        {
            // нет прав на %APPDATA% — молча продолжаем в памяти
        }
    }

    /// <summary>System.Text.Json создаёт коллекции с компаратором по умолчанию — вернём регистронезависимость.</summary>
    private StrategyPreferences Normalized()
    {
        Favorites = Favorites is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(Favorites, StringComparer.OrdinalIgnoreCase);

        SuccessSeconds = SuccessSeconds is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(SuccessSeconds, StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(LastWorking))
            LastWorking = null;

        return this;
    }
}

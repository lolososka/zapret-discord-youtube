using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly SemaphoreSlim _bypassOperationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _shutdownSync = new();
    private CancellationTokenSource? _diagnosticsCancellation;
    private int _slowTick;
    private bool _isBypassOperationActive;
    private bool _isShuttingDown;
    private Task? _shutdownTask;
    private StrategyTestRun? _activeTestRun;

    // «Работала у вас» ставится один раз за запуск — иначе тост повторялся бы каждую секунду.
    private string? _markedThisRun;
    private bool _prefsDirty;

    private AppState()
    {
        Strategies = new ObservableCollection<Strategy>();
        Log = new ObservableCollection<LogLine>();
        Diagnostics = new ObservableCollection<CheckResult>();
        Probes = new ObservableCollection<ProbeResult>();

        ToggleBypassCommand = new AsyncRelayCommand(
            () => RunBypassOperationAsync(ToggleBypassAsync),
            () => CanStartBypassOperation && (IsRunning || SelectedStrategy is not null));
        ApplySelectedStrategyCommand = new AsyncRelayCommand(
            () => RunBypassOperationAsync(ApplySelectedStrategyAsync),
            () => CanApplySelectedStrategy);
        InstallServiceCommand = new AsyncRelayCommand(
            () => RunBypassOperationAsync(InstallServiceAsync),
            () => CanStartBypassOperation && SelectedStrategy is not null);
        RemoveServiceCommand = new AsyncRelayCommand(
            () => RunBypassOperationAsync(RemoveServiceAsync),
            () => CanStartBypassOperation);
        RunDiagnosticsCommand = new AsyncRelayCommand(
            RunDiagnosticsAsync,
            () => !IsDiagnosticsRunning && !_isShuttingDown);
        CancelDiagnosticsCommand = new RelayCommand(
            CancelDiagnostics,
            () => IsDiagnosticsRunning &&
                  !_isShuttingDown &&
                  _diagnosticsCancellation is { IsCancellationRequested: false });
        RunProbesCommand = new AsyncRelayCommand(RunProbesAsync);
        UpdateIpsetCommand = new AsyncRelayCommand(UpdateIpsetAsync);
        CheckHostsCommand = new AsyncRelayCommand(CheckHostsAsync);
        CheckUpdatesCommand = new AsyncRelayCommand(() => CheckUpdatesAsync(silent: false));
        ClearLogCommand = new RelayCommand(() => { Log.Clear(); Raise(nameof(HasLog)); });
        OpenFolderCommand = new RelayCommand(() => OpenExternal(AppPaths.Root));

        AutoPickCommand = new AsyncRelayCommand(
            () => RunBypassOperationAsync(AutoPickAsync),
            () => CanStartBypassOperation && !Tester.IsRunning && Strategies.Count > 0);
        CancelAutoPickCommand = new RelayCommand(() => Tester.Cancel(), () => Tester.IsRunning);

        Tester.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StrategyTester.Best) or nameof(StrategyTester.CanApplyBest))
                Raise(nameof(CanApplyBestTestedStrategy));
            if (e.PropertyName != nameof(StrategyTester.IsRunning)) return;
            CancelAutoPickCommand.RaiseCanExecuteChanged();
            RaiseBypassOperationCanExecuteChanged();
            RaiseMany(nameof(CanApplySelectedStrategy), nameof(StrategyActionText), nameof(StrategyActionHint));
        };
        Tester.RunStarted += OnStrategyTestRunStarted;
        Tester.TrialCompleted += OnStrategyTrialCompleted;
        Tester.RunFinished += OnStrategyTestRunFinished;

        _bypass.StateChanged += (_, _) => OnBypassStateChanged();
        _bypass.LogWritten += (_, line) => AppendLog(line);
        _bypass.AutoRestartRequested = QueueAutoRestartAsync;

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
            RaiseMany(nameof(SelectedStrategyName), nameof(SelectedStrategySummary),
                      nameof(IsSelectedStrategyActive), nameof(CanApplySelectedStrategy),
                      nameof(StrategyActionText), nameof(StrategyActionHint));
            RaiseBypassOperationCanExecuteChanged();
        }
    }

    public string SelectedStrategyName => SelectedStrategy?.DisplayName ?? "Стратегия не выбрана";
    public string SelectedStrategySummary => SelectedStrategy?.Summary ?? "Откройте «Стратегии» и выберите вариант обхода";

    /// <summary>Выбрана именно та конфигурация, которой сейчас владеет GUI.</summary>
    public bool IsSelectedStrategyActive =>
        IsRunning
        && SameStrategy(SelectedStrategy, _bypass.ActiveStrategy)
        && GameFilter == _bypass.ActiveGameFilterMode;

    public bool CanApplySelectedStrategy =>
        SelectedStrategy is not null
        && CanStartBypassOperation
        && !IsApplyingStrategy
        && !IsBusy
        && !Tester.IsRunning
        && !IsSelectedStrategyActive
        && !(IsRunning && _bypass.ActiveStrategy is null);

    public string StrategyActionText
    {
        get
        {
            if (IsApplyingStrategy) return "Применяем…";
            if (_isBypassOperationActive) return "Другая операция…";
            if (_isShuttingDown) return "Завершение…";
            if (IsRunning && _bypass.ActiveStrategy is null) return "Служба активна";
            if (IsSelectedStrategyActive) return "Уже запущена";
            if (IsRunning && SameStrategy(SelectedStrategy, _bypass.ActiveStrategy))
                return "Перезапустить";
            return IsRunning ? "Переключить" : "Запустить";
        }
    }

    public string StrategyActionHint
    {
        get
        {
            if (IsApplyingStrategy) return "Дождитесь завершения переключения";
            if (_isBypassOperationActive) return "Дождитесь завершения текущей операции с обходом";
            if (_isShuttingDown) return "Приложение завершает работу";
            if (IsRunning && _bypass.ActiveStrategy is null)
                return "Обход запущен службой или другой программой — смените профиль через управление службой";
            if (IsSelectedStrategyActive) return "Эта стратегия уже используется";
            if (IsRunning && SameStrategy(SelectedStrategy, _bypass.ActiveStrategy))
                return "Перезапустить эту стратегию с новым режимом игрового фильтра; при ошибке прежний режим будет восстановлен";
            if (IsRunning)
                return "Перезапустить обход с выбранной стратегией; при ошибке прежняя будет восстановлена";
            return "Запустить обход с выбранной стратегией";
        }
    }

    // ---------- Состояние обхода ----------

    public BypassState BypassState => _bypass.State;
    public bool IsRunning => _bypass.State is BypassState.Running;
    public bool IsBusy => _bypass.State is BypassState.Starting or BypassState.Stopping;

    private bool CanStartBypassOperation =>
        !_isBypassOperationActive && !_isShuttingDown;

    private bool _isApplyingStrategy;
    public bool IsApplyingStrategy
    {
        get => _isApplyingStrategy;
        private set
        {
            if (!Set(ref _isApplyingStrategy, value)) return;
            RaiseMany(nameof(CanApplySelectedStrategy), nameof(CanApplyBestTestedStrategy),
                      nameof(StrategyActionText), nameof(StrategyActionHint));
            ApplySelectedStrategyCommand.RaiseCanExecuteChanged();
        }
    }

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
        private set
        {
            if (!Set(ref _serviceState, value)) return;
            RaiseMany(nameof(ServiceStateText), nameof(IsServiceInstalled),
                      nameof(CanApplySelectedStrategy), nameof(StrategyActionText),
                      nameof(StrategyActionHint));
            RaiseBypassOperationCanExecuteChanged();
        }
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
        set => SetGameFilter(value, notifyRunning: true);
    }

    private void SetGameFilter(GameFilterMode value, bool notifyRunning)
    {
        if (!Set(ref _gameFilter, value, nameof(GameFilter))) return;
        FeatureFlags.SetGameFilter(value);
        RaiseMany(nameof(GameFilterText), nameof(IsGameFilterOn),
                  nameof(IsSelectedStrategyActive), nameof(CanApplySelectedStrategy),
                  nameof(StrategyActionText), nameof(StrategyActionHint));
        RaiseBypassOperationCanExecuteChanged();
        if (notifyRunning && IsRunning)
            Notify("Игровой фильтр изменён — перезапустите обход", ToastKind.Warning);
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
    public bool IsDiagnosticsRunning
    {
        get => _isDiagnosticsRunning;
        private set
        {
            if (!Set(ref _isDiagnosticsRunning, value)) return;
            RaiseDiagnosticsCanExecuteChanged();
        }
    }

    private bool _isProbing;
    public bool IsProbing
    {
        get => _isProbing;
        private set
        {
            if (!Set(ref _isProbing, value)) return;
            Raise(nameof(ProbeActionText));
            Raise(nameof(ProbeEmptyText));
        }
    }

    public string ProbeActionText => IsProbing ? "Проверяем…" : "Проверить";
    public string ProbeEmptyText => IsProbing
        ? "Проверяем соединение с Discord и YouTube…"
        : "Проверка ещё не выполнялась — нажмите «Проверить».";

    private string? _busyMessage;
    public string? BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }

    // ---------- Команды ----------

    public AsyncRelayCommand ToggleBypassCommand { get; }
    public AsyncRelayCommand ApplySelectedStrategyCommand { get; }
    public AsyncRelayCommand InstallServiceCommand { get; }
    public AsyncRelayCommand RemoveServiceCommand { get; }
    public AsyncRelayCommand RunDiagnosticsCommand { get; }
    public RelayCommand CancelDiagnosticsCommand { get; }
    public AsyncRelayCommand RunProbesCommand { get; }
    public AsyncRelayCommand UpdateIpsetCommand { get; }
    public AsyncRelayCommand CheckHostsCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Перебирает все стратегии и оставляет лучшую в Tester.Best.</summary>
    public AsyncRelayCommand AutoPickCommand { get; }
    public RelayCommand CancelAutoPickCommand { get; }

    /// <summary>
    /// Все операции, которые могут запускать/останавливать winws.exe или менять службу,
    /// проходят через один gate. Отдельной сериализации внутри AsyncRelayCommand недостаточно:
    /// разные команды иначе могли вклиниться между неудачным запуском и откатом.
    /// </summary>
    private async Task RunBypassOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (operation is null || _isShuttingDown) return;

        await _bypassOperationGate.WaitAsync();
        try
        {
            if (_isShuttingDown) return;

            SetBypassOperationActive(true);
            try
            {
                await operation(_shutdownCancellation.Token);
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
            {
                // Shutdown ждёт gate и сам выполняет последнюю owned-only остановку.
            }
            finally
            {
                SetBypassOperationActive(false);
            }
        }
        finally
        {
            _bypassOperationGate.Release();
        }
    }

    public async Task<CheckFixResult> ApplyDiagnosticFixAsync(
        CheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var fix = result.Fix;
        if (fix is null)
            return new CheckFixResult(false, "Автоматическое исправление больше недоступно.");
        if (_isShuttingDown)
            return new CheckFixResult(false, "Приложение завершает работу.");

        try
        {
            await _bypassOperationGate.WaitAsync(_shutdownCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return new CheckFixResult(false, "Исправление отменено при завершении работы.");
        }

        try
        {
            if (_isShuttingDown)
                return new CheckFixResult(false, "Приложение завершает работу.");

            SetBypassOperationActive(true);
            if (result.RequiresStoppedBypass)
            {
                if (!ProcessUtil.TryIsProcessRunning("winws.exe", out var running))
                {
                    return new CheckFixResult(
                        false,
                        "Не удалось безопасно проверить winws.exe. Удаление служб отменено.");
                }
                if (running)
                {
                    return new CheckFixResult(
                        false,
                        "Сначала остановите все работающие обходы, затем повторите исправление.");
                }
            }

            return await fix();
        }
        finally
        {
            SetBypassOperationActive(false);
            _bypassOperationGate.Release();
        }
    }

    private void SetBypassOperationActive(bool active)
    {
        if (_isBypassOperationActive == active) return;
        _isBypassOperationActive = active;
        RaiseMany(nameof(CanApplySelectedStrategy), nameof(StrategyActionText), nameof(StrategyActionHint));
        RaiseBypassOperationCanExecuteChanged();
    }

    private void RaiseBypassOperationCanExecuteChanged()
    {
        ToggleBypassCommand.RaiseCanExecuteChanged();
        ApplySelectedStrategyCommand.RaiseCanExecuteChanged();
        InstallServiceCommand.RaiseCanExecuteChanged();
        RemoveServiceCommand.RaiseCanExecuteChanged();
        AutoPickCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanApplyBestTestedStrategy));
    }

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
            s.ApplyAutoPickResult(_prefs.FindCurrentTestResult(s), _prefs.LastTestRun?.Mode ?? GameFilterMode.Disabled);
        }
    }

    private void OnStrategyTestRunStarted(object? sender, StrategyTestRunStartedEventArgs e)
    {
        _activeTestRun = new StrategyTestRun
        {
            SchemaVersion = StrategyTestHistory.CurrentSchemaVersion,
            StartedAtUtc = e.StartedAtUtc,
            Mode = e.Mode,
            Status = StrategyTestRunStatus.Running,
            TotalStrategies = e.TotalStrategies,
            ProbeSuiteFingerprint = StrategyTestHistory.CurrentProbeSuiteFingerprint(),
        };

        foreach (var strategy in Strategies)
            strategy.ApplyAutoPickResult(null, e.Mode);
        StrategyMarksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStrategyTrialCompleted(object? sender, StrategyTrialCompletedEventArgs e)
    {
        var run = _activeTestRun;
        var trial = e.Trial;
        if (run is null || trial.Strategy is null)
            return;

        var detail = trial.Detail?.Trim() ?? string.Empty;
        if (detail.Length > StrategyTestHistory.MaxDetailLength)
            detail = detail[..StrategyTestHistory.MaxDetailLength];

        var result = new StrategyTestResult
        {
            StrategyName = trial.Strategy.Name,
            StrategyFingerprint = StrategyTestHistory.Fingerprint(trial.Strategy),
            TestedAtUtc = trial.TestedAtUtc,
            OkCount = trial.OkCount,
            TotalCount = trial.TotalCount,
            AverageLatencyMs = trial.AverageLatencyMs,
            Detail = detail,
        };

        run.Results.RemoveAll(item => string.Equals(
            item.StrategyName,
            result.StrategyName,
            StringComparison.OrdinalIgnoreCase));
        run.Results.Add(result);
        _prefs.LastTestRun = run;
        trial.Strategy.ApplyAutoPickResult(result, run.Mode);
        _prefs.Save();
        StrategyMarksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStrategyTestRunFinished(object? sender, StrategyTestRunFinishedEventArgs e)
    {
        var run = _activeTestRun;
        _activeTestRun = null;
        if (run is null)
            return;

        // Мгновенная отмена не уничтожает предыдущий полезный замер.
        if (run.Results.Count == 0)
        {
            ApplyStrategyMarks();
            RestoreStrategyTestHistory();
            StrategyMarksChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        run.FinishedAtUtc = e.FinishedAtUtc;
        run.Status = e.Status == StrategyTestRunStatus.Completed &&
                     run.Results.Count != run.TotalStrategies
            ? StrategyTestRunStatus.Failed
            : e.Status;
        _prefs.LastTestRun = run;
        _prefs.Save();
        RestoreStrategyTestHistory();
        StrategyMarksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreStrategyTestHistory()
    {
        var run = _prefs.LastTestRun;
        if (run is null)
        {
            Tester.RestoreHistory(Array.Empty<StrategyTrial>(), null);
            return;
        }

        if (!StrategyTestHistory.UsesCurrentProbeSuite(run))
        {
            Tester.RestoreHistory(
                Array.Empty<StrategyTrial>(),
                run,
                "Набор проверок изменился после обновления — запустите автоподбор снова");
            return;
        }

        Tester.RestoreHistory(_prefs.CreateCurrentTrials(Strategies), run);
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

    private async Task AutoPickAsync(CancellationToken ct)
    {
        if (Tester.IsRunning) return;

        _bypass.RefreshState();
        if (!await EnsureManualStartAllowedAsync(ct))
            return;

        if (Strategies.Count == 0)
        {
            Notify("Стратегии не найдены — проверьте папку zapret", ToastKind.Warning);
            return;
        }

        Notify("Перебор начат: обход будет перезапускаться на каждой стратегии", ToastKind.Info);

        var outcome = await Tester.RunAsync(Strategies.ToList(), GameFilter, ct);
        if (ct.IsCancellationRequested || _isShuttingDown) return;
        if (outcome == StrategyTestRunStatus.Cancelled)
        {
            Notify("Автоподбор остановлен — сохранённые результаты не потеряны", ToastKind.Info);
            return;
        }
        if (outcome != StrategyTestRunStatus.Completed)
        {
            Notify("Автоподбор прервался — прежние результаты не потеряны", ToastKind.Warning);
            return;
        }

        var best = Tester.Best;
        if (best is null)
        {
            Notify("Ни одна стратегия не открыла сайты — загляните в «Диагностику»", ToastKind.Warning);
            return;
        }

        Notify(
            best.Success
                ? $"Лучший результат: {best.Title} — Discord и YouTube доступны"
                : $"Полностью рабочая стратегия не найдена. Лучший частичный результат: {best.Title} — {best.ScoreText}",
            best.Success ? ToastKind.Success : ToastKind.Warning);
    }

    /// <summary>
    /// Применяет пару «стратегия + игровой режим», которая действительно была проверена.
    /// Проверка выполняется до изменения сохранённого выбора, поэтому заблокированная
    /// операция не оставляет после себя неожиданные настройки.
    /// </summary>
    public bool TryApplyTestedStrategy(StrategyTrial? trial)
    {
        if (trial?.Strategy is not { } strategy)
        {
            Notify("Перебор ещё не нашёл рабочую стратегию", ToastKind.Warning);
            return false;
        }

        if (!CanApplyStrategy(strategy, trial.Mode))
        {
            var reason = _isShuttingDown
                ? "Приложение завершает работу"
                : Tester.IsRunning
                    ? "Дождитесь завершения автоподбора"
                    : IsBusy || IsApplyingStrategy || _isBypassOperationActive
                        ? "Дождитесь завершения текущей операции"
                        : IsRunning && _bypass.ActiveStrategy is null
                            ? "Сначала остановите службу или внешний обход"
                            : "Эта стратегия с проверенным режимом уже запущена";
            Notify(reason, ToastKind.Warning);
            return false;
        }

        // Процесс уже может работать ровно в проверенной конфигурации, пока выбор в UI
        // был изменён без перезапуска. В этом случае достаточно синхронизировать настройки.
        if (IsRunning && SameStrategy(strategy, _bypass.ActiveStrategy) &&
            trial.Mode == _bypass.ActiveGameFilterMode)
        {
            SelectedStrategy = strategy;
            SetGameFilter(trial.Mode, notifyRunning: false);
            Notify("Проверенная конфигурация уже запущена — настройки синхронизированы", ToastKind.Info);
            return true;
        }

        var previousStrategy = SelectedStrategy;
        var previousMode = GameFilter;
        SelectedStrategy = strategy;
        SetGameFilter(trial.Mode, notifyRunning: false);

        if (!ApplySelectedStrategyCommand.CanExecute(null))
        {
            SelectedStrategy = previousStrategy;
            SetGameFilter(previousMode, notifyRunning: false);
            Notify("Не удалось начать переключение — повторите через несколько секунд", ToastKind.Warning);
            return false;
        }

        ApplySelectedStrategyCommand.Execute(null);
        return true;
    }

    public bool CanApplyBestTestedStrategy => Tester.Best is { } best &&
                                               CanApplyStrategy(best.Strategy, best.Mode);

    private bool CanApplyStrategy(Strategy strategy, GameFilterMode mode)
        => strategy is not null
           && CanStartBypassOperation
           && !IsApplyingStrategy
           && !IsBusy
           && !Tester.IsRunning
           && !(IsRunning && _bypass.ActiveStrategy is null)
           && !(IsRunning && SameStrategy(strategy, _bypass.ActiveStrategy) &&
                mode == _bypass.ActiveGameFilterMode &&
                SameStrategy(SelectedStrategy, strategy) && GameFilter == mode);

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

        if (AppSettings.Current.AutoStartBypass &&
            !App.PostUpdateHealthCheckRequested &&
            !IsRunning &&
            SelectedStrategy is not null)
            await RunBypassOperationAsync(ToggleBypassAsync);

        if (AppSettings.Current.CheckUpdatesOnLaunch)
            _ = CheckUpdatesAsync(silent: true);
    }

    public void ReloadStrategies()
    {
        var list = StrategyParser.LoadAll();
        Strategies.Clear();
        foreach (var s in list) Strategies.Add(s);

        ApplyStrategyMarks();
        RestoreStrategyTestHistory();

        // Приоритет подсказок: выбор пользователя → то, что у него уже работало → избранное → обычный старт.
        var wanted = AppSettings.Current.LastStrategy;
        var pick = list.FirstOrDefault(s => s.Name == wanted)
                   ?? list.FirstOrDefault(s => s.Name == _prefs.LastWorking)
                   ?? list.FirstOrDefault(s => s.IsFavorite)
                   ?? list.FirstOrDefault(s => s.IsRecommended)
                   ?? list.FirstOrDefault();

        _selectedStrategy = pick;
        RaiseMany(nameof(SelectedStrategy), nameof(SelectedStrategyName), nameof(SelectedStrategySummary),
                  nameof(IsSelectedStrategyActive), nameof(CanApplySelectedStrategy),
                  nameof(StrategyActionText), nameof(StrategyActionHint));
        RaiseBypassOperationCanExecuteChanged();
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownSync)
            return _shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        _isShuttingDown = true;
        _shutdownCancellation.Cancel();
        RaiseDiagnosticsCanExecuteChanged();
        _ticker.Stop();
        Traffic.Stop();
        Tester.Cancel();
        RaiseMany(nameof(CanApplySelectedStrategy), nameof(StrategyActionText), nameof(StrategyActionHint));
        RaiseBypassOperationCanExecuteChanged();

        // Текущая операция удерживает gate до полного завершения. После отмены
        // автоподбор остановится, а Apply не начнёт fallback.
        await _bypassOperationGate.WaitAsync();
        try
        {
            if (_prefsDirty)
            {
                _prefsDirty = false;
                _prefs.Save();
            }

            // Финальная остановка выполняется под тем же gate: после неё новый
            // принадлежащий GUI процесс уже не сможет появиться.
            await _bypass.StopAsync(ownedOnly: true);
        }
        finally
        {
            _bypassOperationGate.Release();
        }
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
                  nameof(StatusTitle), nameof(StatusSubtitle), nameof(UptimeText),
                  nameof(IsSelectedStrategyActive), nameof(CanApplySelectedStrategy),
                  nameof(StrategyActionText), nameof(StrategyActionHint));
        RaiseBypassOperationCanExecuteChanged();

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

    /// <summary>
    /// Перед запуском без собственного активного процесса перечитываем службу и состояние
    /// процессов. Кэш ServiceState обновляется раз в пять секунд и для решения о запуске
    /// недостаточно надёжен.
    /// </summary>
    private async Task<bool> EnsureManualStartAllowedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await RefreshServiceStateAsync();
        ct.ThrowIfCancellationRequested();

        _bypass.RefreshState();
        if (ServiceState is ServiceState.Running or ServiceState.Pending)
        {
            Notify(
                ServiceState == ServiceState.Running
                    ? "Служба zapret уже работает — сначала остановите или переустановите её"
                    : "Служба zapret меняет состояние — дождитесь завершения операции",
                ToastKind.Warning);
            return false;
        }

        if (_bypass.State is BypassState.Running && _bypass.ActiveStrategy is null)
        {
            Notify(
                "Уже работает winws.exe, запущенный другой программой. Zapret GUI не будет его завершать.",
                ToastKind.Warning);
            return false;
        }

        return true;
    }

    private async Task AutoRestartBypassAsync(
        Strategy strategy,
        GameFilterMode mode,
        CancellationToken ct,
        bool fromAutoRestart = false)
    {
        ct.ThrowIfCancellationRequested();
        _bypass.RefreshState();
        if (_bypass.State is BypassState.Running or BypassState.Starting)
            return;
        if (!await EnsureManualStartAllowedAsync(ct))
        {
            _bypass.Log(
                "Автоперезапуск отменён: служба или сторонний winws.exe уже заняли WinDivert.",
                LogLevel.Warn);
            return;
        }

        ct.ThrowIfCancellationRequested();
        _bypass.RefreshState();
        if (_bypass.State is BypassState.Running or BypassState.Starting)
            return;
        await _bypass.StartAsync(strategy, mode, ct, fromAutoRestart);
    }

    private Task QueueAutoRestartAsync(
        Strategy strategy,
        GameFilterMode mode,
        CancellationToken restartCancellation)
    {
        // Во время автоподбора процессами управляет сам StrategyTester. Иначе падение
        // одной тестовой стратегии ставит обычный автоперезапуск в очередь и после
        // завершения перебора неожиданно включает уже проверенный временный профиль.
        if (Tester.IsRunning)
        {
            _bypass.Log("Автоперезапуск пропущен: сейчас идёт автоподбор стратегий.", LogLevel.Info);
            return Task.CompletedTask;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return Task.CompletedTask;
        if (dispatcher.CheckAccess())
        {
            return RunBypassOperationAsync(
                ct => RunAutoRestartWithLinkedCancellationAsync(
                    strategy,
                    mode,
                    ct,
                    restartCancellation));
        }

        return dispatcher.InvokeAsync(
                () => RunBypassOperationAsync(
                    ct => RunAutoRestartWithLinkedCancellationAsync(
                        strategy,
                        mode,
                        ct,
                        restartCancellation)))
            .Task
            .Unwrap();
    }

    private async Task RunAutoRestartWithLinkedCancellationAsync(
        Strategy strategy,
        GameFilterMode mode,
        CancellationToken appCancellation,
        CancellationToken restartCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            appCancellation,
            restartCancellation);
        try
        {
            await AutoRestartBypassAsync(strategy, mode, linked.Token, fromAutoRestart: true);
        }
        catch (OperationCanceledException) when (restartCancellation.IsCancellationRequested)
        {
            // Пользователь остановил или запустил обход вручную: старый recovery больше не нужен.
        }
    }

    private void NotifyManualStartResult(bool started, Strategy target)
    {
        if (started)
        {
            Notify($"Обход запущен · {target.DisplayName}", ToastKind.Success);
            return;
        }

        if (_bypass.State is BypassState.Running && _bypass.ActiveStrategy is null)
        {
            Notify(
                "Запуск отменён: обнаружен чужой или служебный winws.exe",
                ToastKind.Warning);
            return;
        }

        Notify("Запуск не удался — смотрите журнал", ToastKind.Error);
    }

    // ---------- Реализация команд ----------

    /// <summary>
    /// Запускает выбранную стратегию. Если GUI уже владеет работающим winws.exe, операция
    /// становится переключением: при неудачном старте возвращаются прежняя стратегия и
    /// прежний режим игрового фильтра.
    /// </summary>
    private async Task ApplySelectedStrategyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = SelectedStrategy;
        if (target is null || IsApplyingStrategy) return;

        _bypass.RefreshState();
        var previous = _bypass.ActiveStrategy;
        var wasRunning = IsRunning;
        var previousMode = _bypass.ActiveGameFilterMode;

        if (previous is null)
        {
            if (!await EnsureManualStartAllowedAsync(ct))
                return;

            // Состояние могло измениться, пока выполнялся запрос к SCM.
            previous = _bypass.ActiveStrategy;
            wasRunning = IsRunning;
            previousMode = _bypass.ActiveGameFilterMode;
        }

        if (wasRunning && SameStrategy(target, previous) && GameFilter == previousMode)
        {
            Notify($"«{target.DisplayName}» уже запущена", ToastKind.Info);
            return;
        }

        if (wasRunning && previous is null)
        {
            Notify("Обход запущен другой программой — Zapret GUI не будет его завершать",
                   ToastKind.Warning);
            return;
        }

        IsApplyingStrategy = true;
        try
        {
            if (!wasRunning)
            {
                var started = await _bypass.StartAsync(target, GameFilter, ct);
                if (ct.IsCancellationRequested || _isShuttingDown) return;

                NotifyManualStartResult(started, target);

                if (started) _ = TelegramProxy.Instance.StartWithBypassAsync();
                return;
            }

            // Ветка wasRunning гарантирует наличие ActiveStrategy: внешний процесс выше
            // отсекается отдельно и не может участвовать в безопасном откате.
            var fallback = previous!;
            var sameProfile = SameStrategy(target, fallback);
            _bypass.Log(sameProfile
                ? $"Перезапуск «{target.DisplayName}» с новыми параметрами…"
                : $"Переключение с «{fallback.DisplayName}» на «{target.DisplayName}»…");

            var switched = await _bypass.StartAsync(target, GameFilter, ct);
            if (ct.IsCancellationRequested || _isShuttingDown)
            {
                // Отменённый неудачный старт не должен стать следующим AutoStart-профилем.
                // Сам fallback при shutdown не запускаем, возвращаем только сохранённый выбор.
                if (!switched)
                {
                    SelectedStrategy = fallback;
                    SetGameFilter(previousMode, notifyRunning: false);
                }
                return;
            }

            if (switched)
            {
                Notify(sameProfile
                        ? $"Обход перезапущен · {target.DisplayName}"
                        : $"Стратегия переключена · {target.DisplayName}",
                       ToastKind.Success);
                _ = TelegramProxy.Instance.StartWithBypassAsync();
                return;
            }

            // Между остановкой своего процесса и новым стартом мог появиться служебный
            // winws.exe. Контроллер его не трогает, а откат рядом с ним невозможен.
            if (_bypass.State is BypassState.Running && _bypass.ActiveStrategy is null)
            {
                SelectedStrategy = fallback;
                SetGameFilter(previousMode, notifyRunning: false);
                Notify(
                    "Переключение отменено: обнаружен чужой или служебный winws.exe. Прежняя стратегия не запускалась.",
                    ToastKind.Warning);
                return;
            }

            // Выбор в интерфейсе и сохранённый feature flag должны соответствовать
            // конфигурации, которую сейчас будем восстанавливать (или предложим при следующем запуске).
            SelectedStrategy = fallback;
            SetGameFilter(previousMode, notifyRunning: false);

            // Shutdown отменяет token до ожидания общего gate. После этой точки fallback
            // не должен начинаться, иначе он сможет появиться после финального StopAsync.
            if (ct.IsCancellationRequested || _isShuttingDown) return;

            // Не оставляем повреждённый профиль последним выбранным: иначе при следующем
            // AutoStartBypass приложение снова попробует запустить его.
            _bypass.Log(
                $"«{target.DisplayName}» не запустилась. Восстанавливаем «{fallback.DisplayName}»…",
                LogLevel.Warn);

            var restored = await _bypass.StartAsync(fallback, previousMode, ct);
            if (ct.IsCancellationRequested || _isShuttingDown) return;

            if (restored)
            {
                Notify(sameProfile
                        ? "Новые параметры не применились — прежняя конфигурация восстановлена"
                        : $"«{target.DisplayName}» не запустилась — восстановлена «{fallback.DisplayName}»",
                       ToastKind.Warning);
                return;
            }

            Notify(
                $"Не удалось запустить «{target.DisplayName}» и восстановить «{fallback.DisplayName}». Обход выключен — откройте журнал.",
                ToastKind.Error);
        }
        finally
        {
            IsApplyingStrategy = false;
        }
    }

    private async Task ToggleBypassAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _bypass.RefreshState();

        if (IsRunning)
        {
            if (_bypass.ActiveStrategy is null)
            {
                await RefreshServiceStateAsync();
                _bypass.RefreshState();
                Notify(
                    ServiceState is ServiceState.Running or ServiceState.Pending
                        ? "winws.exe управляется службой zapret — используйте управление службой"
                        : "winws.exe запущен другой программой — Zapret GUI не будет его завершать",
                    ToastKind.Warning);
                return;
            }

            await _bypass.StopAsync();
            if (ct.IsCancellationRequested || _isShuttingDown) return;
            Notify(
                IsRunning && _bypass.ActiveStrategy is null
                    ? "Свой обход остановлен, но чужой или служебный winws.exe продолжает работать"
                    : "Обход остановлен",
                IsRunning && _bypass.ActiveStrategy is null ? ToastKind.Warning : ToastKind.Info);
            return;
        }

        if (SelectedStrategy is null) return;
        if (!await EnsureManualStartAllowedAsync(ct))
            return;

        var target = SelectedStrategy;
        var ok = await _bypass.StartAsync(target, GameFilter, ct);
        if (ct.IsCancellationRequested || _isShuttingDown) return;

        NotifyManualStartResult(ok, target);

        // Прокси Telegram поднимается следом, если пользователь включил это на странице «Телеграм».
        // Обратно вместе с обходом он не гасится: утилита самостоятельная и живёт в трее.
        if (ok) _ = TelegramProxy.Instance.StartWithBypassAsync();
    }

    private async Task InstallServiceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = SelectedStrategy;
        if (target is null) return;
        var mode = GameFilter;

        BusyMessage = "Устанавливаем службу…";
        try
        {
            await RefreshServiceStateAsync();
            ct.ThrowIfCancellationRequested();
            _bypass.RefreshState();
            if (ServiceState is ServiceState.Pending or ServiceState.Unknown)
            {
                Notify(
                    "Состояние службы ещё не определено — дождитесь обновления статуса",
                    ToastKind.Warning);
                return;
            }
            if (ServiceState != ServiceState.Running &&
                _bypass.State is BypassState.Running &&
                _bypass.ActiveStrategy is null)
            {
                Notify(
                    "Служба не установлена: уже работает сторонний winws.exe, и GUI не будет его завершать",
                    ToastKind.Warning);
                return;
            }

            await _bypass.StopAsync();
            ct.ThrowIfCancellationRequested();

            var r = await ZapretServiceManager.InstallAsync(target, mode);
            _bypass.Log(r.Output, r.Success ? LogLevel.Success : LogLevel.Error);
            Notify(r.Success ? "Служба установлена и запущена" : "Не удалось установить службу",
                   r.Success ? ToastKind.Success : ToastKind.Error);
            await RefreshServiceStateAsync();
            _bypass.RefreshState();
        }
        finally { BusyMessage = null; }
    }

    private async Task RemoveServiceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
        if (IsDiagnosticsRunning || _isShuttingDown) return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
        _diagnosticsCancellation = cancellation;
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
            await DiagnosticsRunner.RunAllAsync(progress, cancellation.Token);

            var bad = Diagnostics.Count(d => d.Status == CheckStatus.Failed);
            var warn = Diagnostics.Count(d => d.Status == CheckStatus.Warning);
            var inconclusive = Diagnostics.Count(d => d.Status == CheckStatus.Inconclusive);
            var badText = $"{bad} {Plural(bad, "проблема", "проблемы", "проблем")}";
            var warnText = $"{warn} {Plural(warn, "предупреждение", "предупреждения", "предупреждений")}";
            var inconclusiveText =
                $"{inconclusive} {Plural(inconclusive, "проверка не завершена", "проверки не завершены", "проверок не завершено")}";
            var summary = new List<string>();
            if (bad > 0) summary.Add(badText);
            if (warn > 0) summary.Add(warnText);
            if (inconclusive > 0) summary.Add(inconclusiveText);

            Notify(summary.Count > 0
                    ? "Диагностика: " + string.Join(", ", summary)
                    : "Диагностика: всё в порядке",
                   bad > 0
                       ? ToastKind.Error
                       : warn > 0 || inconclusive > 0
                           ? ToastKind.Warning
                           : ToastKind.Success);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_isShuttingDown)
                Notify("Диагностика отменена.", ToastKind.Info);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
            if (!_isShuttingDown)
                Notify("Диагностику не удалось завершить: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            if (ReferenceEquals(_diagnosticsCancellation, cancellation))
                _diagnosticsCancellation = null;
            IsDiagnosticsRunning = false;
        }
    }

    private void CancelDiagnostics()
    {
        var cancellation = _diagnosticsCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested) return;

        cancellation.Cancel();
        CancelDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    private void RaiseDiagnosticsCanExecuteChanged()
    {
        RunDiagnosticsCommand.RaiseCanExecuteChanged();
        CancelDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    private async Task RunProbesAsync()
    {
        if (IsProbing) return;
        IsProbing = true;
        Probes.Clear();
        try
        {
            var results = await ConnectivityTester.ProbeAllAsync(
                ConnectivityTester.Sites,
                _shutdownCancellation.Token);

            // Task.WhenAll сохраняет исходный порядок — строки больше не прыгают
            // в зависимости от того, какой сайт ответил первым.
            foreach (var result in results)
                Probes.Add(result);

            var targets = results.Where(result => result.Site.CountsTowardStrategyScore).ToArray();
            int opened = targets.Count(result => result.Ok);
            int extraFailures = results.Count(result => !result.Site.CountsTowardStrategyScore && !result.Ok);
            if (opened == targets.Length)
            {
                var extra = extraFailures == 0
                    ? string.Empty
                    : $" Дополнительных сбоев: {extraFailures}.";
                Notify("Discord и YouTube доступны." + extra, ToastKind.Success);
            }
            else
            {
                Notify(
                    $"Открылись {opened} из {targets.Length} основных адресов. Причины показаны в списке.",
                    ToastKind.Warning);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            // Приложение закрывается — результат уже не нужен.
        }
        catch (Exception ex)
        {
            _bypass.Log("Проверка соединения завершилась ошибкой: " + ex.Message, LogLevel.Error);
            Notify("Не удалось завершить проверку соединения", ToastKind.Error);
        }
        finally
        {
            IsProbing = false;
        }
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

    private static bool SameStrategy(Strategy? left, Strategy? right)
    {
        if (ReferenceEquals(left, right)) return left is not null;
        if (left is null || right is null) return false;

        return string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Последний автоподбор хотя бы с одним замером. DTO не содержит путей и полных команд.</summary>
    public StrategyTestRun? LastTestRun { get; set; }

    private static readonly object Sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    static StrategyPreferences()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "strategies.json");

    public static StrategyPreferences Load()
        => Load(FilePath);

    public static StrategyPreferences Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    return FromJson(json);
            }
        }
        catch
        {
            // повреждённый файл — начинаем с чистого листа
        }

        return new StrategyPreferences();
    }

    public static StrategyPreferences FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new StrategyPreferences();

        try
        {
            var loaded = JsonSerializer.Deserialize<StrategyPreferences>(json, JsonOptions);
            if (loaded is not null)
                return loaded.Normalized();
        }
        catch
        {
            // Повреждённое поле истории не должно уничтожать избранное и накопленное время.
        }

        return ReadBasePreferencesTolerantly(json);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Никогда не бросает: потеря звёздочек не повод падать.</summary>
    public void Save()
        => Save(FilePath);

    public void Save(string path)
    {
        string? temporaryPath = null;
        try
        {
            lock (Sync)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                temporaryPath = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryPath, ToJson(), new System.Text.UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                {
                    try { File.Move(temporaryPath, path); }
                    catch (IOException) when (File.Exists(path))
                    {
                        File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                }
                temporaryPath = null;
            }
        }
        catch
        {
            // нет прав на %APPDATA% — молча продолжаем в памяти
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    public StrategyTestResult? FindCurrentTestResult(Strategy strategy)
    {
        var run = LastTestRun;
        if (strategy is null || run is null || !StrategyTestHistory.UsesCurrentProbeSuite(run))
            return null;

        foreach (var result in run.Results)
            if (result.TotalCount == ConnectivityTester.ScoredSiteCount &&
                StrategyTestHistory.Matches(strategy, result))
                return result;

        return null;
    }

    public IReadOnlyList<StrategyTrial> CreateCurrentTrials(IEnumerable<Strategy> strategies)
    {
        var run = LastTestRun;
        if (run is null || !StrategyTestHistory.UsesCurrentProbeSuite(run))
            return Array.Empty<StrategyTrial>();

        var byName = new Dictionary<string, Strategy>(StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in strategies ?? Array.Empty<Strategy>())
            if (strategy is not null && !string.IsNullOrWhiteSpace(strategy.Name))
                byName.TryAdd(strategy.Name, strategy);

        var trials = new List<StrategyTrial>();
        foreach (var result in run.Results)
        {
            if (!byName.TryGetValue(result.StrategyName, out var strategy) ||
                result.TotalCount != ConnectivityTester.ScoredSiteCount ||
                !StrategyTestHistory.Matches(strategy, result))
                continue;

            trials.Add(new StrategyTrial(
                strategy,
                result.OkCount == result.TotalCount,
                result.OkCount,
                result.TotalCount,
                result.AverageLatencyMs,
                result.Detail,
                result.TestedAtUtc,
                run.Mode));
        }

        return trials;
    }

    /// <summary>System.Text.Json создаёт коллекции с компаратором по умолчанию — вернём регистронезависимость.</summary>
    private StrategyPreferences Normalized()
    {
        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Favorites ?? new HashSet<string>())
            if (!string.IsNullOrWhiteSpace(name) && name.Length <= 260)
                favorites.Add(name.Trim());
        Favorites = favorites;

        var seconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in SuccessSeconds ?? new Dictionary<string, int>())
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 260 && pair.Value >= 0)
                seconds[pair.Key.Trim()] = pair.Value;
        SuccessSeconds = seconds;

        if (string.IsNullOrWhiteSpace(LastWorking))
            LastWorking = null;
        else
            LastWorking = LastWorking.Trim();

        LastTestRun = StrategyTestHistory.Normalize(LastTestRun);

        return this;
    }

    private static StrategyPreferences ReadBasePreferencesTolerantly(string json)
    {
        var result = new StrategyPreferences();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            var root = document.RootElement;
            if (TryGetProperty(root, nameof(Favorites), out var favorites) &&
                favorites.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in favorites.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                        result.Favorites.Add(name);
            }

            if (TryGetProperty(root, nameof(LastWorking), out var lastWorking) &&
                lastWorking.ValueKind == JsonValueKind.String)
                result.LastWorking = lastWorking.GetString();

            if (TryGetProperty(root, nameof(SuccessSeconds), out var success) &&
                success.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in success.EnumerateObject())
                    if (item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out int value))
                        result.SuccessSeconds[item.Name] = value;
            }

            if (TryGetProperty(root, nameof(LastTestRun), out var history) &&
                history.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                try { result.LastTestRun = history.Deserialize<StrategyTestRun>(JsonOptions); }
                catch { result.LastTestRun = null; }
            }
        }
        catch
        {
            return new StrategyPreferences();
        }

        return result.Normalized();
    }

    private static bool TryGetProperty(JsonElement source, string name, out JsonElement value)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}

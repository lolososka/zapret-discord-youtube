using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ZapretGui.Core;

/// <summary>A user-initiated comparison; never monitors traffic or switches a network silently.</summary>
public sealed class ConnectionAssistant : ObservableObject
{
    private readonly AppState _state;
    private readonly BypassController _bypass = BypassController.Instance;
    private readonly ConnectionProfileStore _store;
    private CancellationTokenSource? _cancellation;
    private NetworkContext _context = new("", "Сеть не определена", false);
    private IReadOnlyList<StrategyTrial> _trials = Array.Empty<StrategyTrial>();
    private StrategyTrial? _recommendation;
    private string _scanNetworkKey = "";
    private string _scanSuite = "";
    private DateTime _scanTime;
    private bool _completedPick;
    private bool _allowCancel;

    public ConnectionAssistant(AppState state)
    {
        _state = state;
        _store = new ConnectionProfileStore(ConnectionProfileStore.DefaultPath);
        CheckCommand = new AsyncRelayCommand(() => ExecuteAsync(ct => CompareAsync(false, ct), allowCancel: true), CanRun);
        PickCommand = new AsyncRelayCommand(() => ExecuteAsync(ct => CompareAsync(true, ct), allowCancel: true),
            () => CanRun() && _state.Strategies.Count > 0);
        ApplyCommand = new AsyncRelayCommand(() => ExecuteAsync(ApplyRecommendationAsync),
            () => CanRun() && IsRecommendationCurrent());
        CancelCommand = new RelayCommand(Cancel, () => CanCancel && _cancellation is { IsCancellationRequested: false });
        SaveProfileCommand = new RelayCommand(SaveProfile, () => CanSaveProfile);
        ApplyProfileCommand = new AsyncRelayCommand(() => ExecuteAsync(ApplyProfileAsync),
            () => CanRun() && FindProfileStrategy(SelectedProfile) is not null);
        RemoveProfileCommand = new RelayCommand(RemoveProfile, () => !IsBusy && SelectedProfile is not null);
        RefreshNetworkCommand = new RelayCommand(RefreshContext, () => !IsBusy);
        _state.Tester.PropertyChanged += OnTesterChanged;
        SetServices(BaselineServices, null);
        SetServices(CurrentServices, null);
    }

    public ObservableCollection<ConnectionServiceResult> BaselineServices { get; } = new();
    public ObservableCollection<ConnectionServiceResult> CurrentServices { get; } = new();
    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RefreshActions(); } }
    public bool CanCancel => IsBusy && _allowCancel;
    private string _statusText = "Проверьте сеть или начните подбор";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    private string _stepText = "Готов к проверке";
    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }
    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string NetworkName => string.IsNullOrWhiteSpace(_context.Name) ? "Сеть не определена" : _context.Name;
    public string RecommendedTitle => _recommendation?.Title ?? "Стратегия ещё не подобрана";
    public string RecommendedScore => _recommendation is null ? "" : $"{_recommendation.ScoreText} · {_recommendation.LatencyText}";
    public bool HasRecommendation => _recommendation is not null;
    private bool _hasBaseline;
    public bool HasBaseline { get => _hasBaseline; private set => Set(ref _hasBaseline, value); }
    private bool _hasCurrent;
    public bool HasCurrent { get => _hasCurrent; private set => Set(ref _hasCurrent, value); }
    private string _profileNameText = "Моя сеть";
    public string ProfileNameText { get => _profileNameText; set { if (Set(ref _profileNameText, value)) RefreshActions(); } }
    private ConnectionProfile? _selectedProfile;
    public ConnectionProfile? SelectedProfile { get => _selectedProfile; set { if (Set(ref _selectedProfile, value)) RefreshActions(); } }
    private ConnectionGoal _goal;
    public ConnectionGoal Goal => _goal;
    public bool GoalAll => Goal == ConnectionGoal.All;
    public bool GoalYouTube => Goal == ConnectionGoal.YouTube;
    public bool GoalDiscord => Goal == ConnectionGoal.Discord;
    public bool CanSaveProfile => !IsBusy && _context.IsAvailable && IsRecommendationCurrent()
                                 && !string.IsNullOrWhiteSpace(ProfileNameText)
                                 && ProfileNameText.Trim().Length <= 64;

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand PickCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand ApplyProfileCommand { get; }
    public RelayCommand RemoveProfileCommand { get; }
    public RelayCommand RefreshNetworkCommand { get; }

    private bool CanRun() => !IsBusy && _state.CanRunAssistant;

    public void SelectGoal(ConnectionGoal goal)
    {
        if (IsBusy || !Enum.IsDefined(goal)) return;
        _goal = goal;
        RaiseMany(nameof(Goal), nameof(GoalAll), nameof(GoalYouTube), nameof(GoalDiscord));
        ChooseRecommendation();
    }

    public void RefreshContext()
    {
        if (IsBusy) return;
        UpdateContext(NetworkContext.Capture());
    }

    private void UpdateContext(NetworkContext context)
    {
        var changed = _context.Key != context.Key;
        _context = context;
        Raise(nameof(NetworkName));
        if (changed && _completedPick)
            StatusText = "Сеть изменилась — повторите подбор";
        var selectedName = SelectedProfile?.StrategyName;
        Profiles.Clear();
        foreach (var profile in _store.Profiles.Where(p => context.IsAvailable && p.NetworkKey == context.Key))
            Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(p => p.StrategyName == selectedName) ?? Profiles.FirstOrDefault();
        RefreshActions();
    }

    public void RefreshActions()
    {
        Raise(nameof(CanSaveProfile));
        Raise(nameof(CanCancel));
        CheckCommand.RaiseCanExecuteChanged();
        PickCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        SaveProfileCommand.RaiseCanExecuteChanged();
        ApplyProfileCommand.RaiseCanExecuteChanged();
        RemoveProfileCommand.RaiseCanExecuteChanged();
        RefreshNetworkCommand.RaiseCanExecuteChanged();
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation, bool allowCancel = false)
    {
        if (!CanRun()) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_state.AssistantShutdownToken);
        _cancellation = cancellation;
        _allowCancel = allowCancel;
        IsBusy = true;
        try
        {
            await _state.RunAssistantOperationAsync(_ => operation(cancellation.Token));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Проверка отменена";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _state.Notify(ex.Message, ToastKind.Error);
        }
        finally
        {
            _cancellation = null;
            _allowCancel = false;
            IsBusy = false;
            StepText = "Готово";
        }
    }

    private async Task CompareAsync(bool pick, CancellationToken ct)
    {
        if (!await _state.PrepareAssistantAsync(ct))
        {
            StatusText = "Проверка не началась — откройте журнал";
            return;
        }
        var previous = _bypass.ActiveStrategy;
        var previousMode = _bypass.ActiveGameFilterMode;
        var wasRunning = _bypass.State == BypassState.Running && previous is not null;
        var requestedMode = _state.GameFilter;
        var context = NetworkContext.Capture();
        UpdateContext(context);
        _completedPick = false;
        _trials = Array.Empty<StrategyTrial>();
        ChooseRecommendation();
        HasBaseline = HasCurrent = false;
        SetServices(BaselineServices, null);
        SetServices(CurrentServices, null);
        Progress = 0;
        _state.Notify("Во время проверки обход будет перезапускаться", ToastKind.Info);
        try
        {
            StepText = "1 / 3 · Без обхода";
            StatusText = "Проверяем прямое соединение…";
            if (!await _bypass.StopAsync())
                throw new InvalidOperationException("Не удалось остановить обход — проверка отменена");
            ct.ThrowIfCancellationRequested();
            var baseline = await ConnectivityTester.ProbeAllAsync(ConnectivityTester.Sites, ct);
            SetServices(BaselineServices, baseline);
            HasBaseline = true;
            EnsureSameNetwork(context);
            Progress = pick ? 0.15 : 0.5;
            if (pick)
            {
                StepText = "2 / 3 · Подбор";
                var outcome = await _state.Tester.RunAsync(_state.Strategies.ToList(), requestedMode, ct);
                ct.ThrowIfCancellationRequested();
                if (outcome != StrategyTestRunStatus.Completed)
                    throw new InvalidOperationException("Подбор не завершён — результаты не применены");
                EnsureSameNetwork(context);
                _trials = _state.Tester.Results.ToArray();
                _scanNetworkKey = context.Key;
                _scanSuite = StrategyTestHistory.CurrentProbeSuiteFingerprint();
                _scanTime = DateTime.UtcNow;
                _completedPick = true;
                ChooseRecommendation();
                StatusText = !context.IsAvailable
                    ? "Windows не определила сеть — запустите результат из раздела «Стратегии»"
                    : HasRecommendation ? "Результат готов — можно запустить стратегию" : "Для выбранного сервиса стратегия не найдена";
            }
            else if (wasRunning)
            {
                StepText = "2 / 3 · Текущая стратегия";
                if (!await _bypass.StartAsync(previous!, previousMode, ct))
                    throw new InvalidOperationException("Не удалось восстановить текущую стратегию");
                var current = await ConnectivityTester.ProbeAllAsync(ConnectivityTester.Sites, ct);
                EnsureSameNetwork(context);
                SetServices(CurrentServices, current);
                HasCurrent = true;
                StatusText = "Сравнение готово";
            }
            else
            {
                StatusText = "Прямое соединение проверено — можно начать подбор";
            }
            Progress = 1;
        }
        finally
        {
            // User cancellation restores the owned session; application shutdown must not restart it.
            if (wasRunning && !_state.IsAssistantShuttingDown &&
                !(_bypass.State == BypassState.Running &&
                  _bypass.ActiveStrategy?.Name == previous!.Name && _bypass.ActiveGameFilterMode == previousMode))
            {
                StepText = "3 / 3 · Восстановление";
                if (!await _bypass.StartAsync(previous!, previousMode, _state.AssistantShutdownToken))
                {
                    _state.Notify("Прежняя стратегия не восстановилась — проверьте журнал", ToastKind.Error);
                    StatusText = "Не удалось восстановить прежнюю стратегию";
                }
            }
        }
    }

    private static void EnsureSameNetwork(NetworkContext initial)
    {
        var current = NetworkContext.Capture();
        if (initial.Key != current.Key || initial.IsAvailable != current.IsAvailable)
            throw new InvalidOperationException("Сеть изменилась во время проверки — повторите подбор");
    }

    private void ChooseRecommendation()
    {
        _recommendation = _completedPick ? ConnectionAssessment.Recommended(_trials, Goal) : null;
        if (_completedPick)
        {
            SetServices(CurrentServices, _recommendation?.Probes);
            HasCurrent = _recommendation?.Probes is { Count: > 0 };
        }
        RaiseMany(nameof(RecommendedTitle), nameof(RecommendedScore), nameof(HasRecommendation));
        RefreshActions();
    }

    private bool IsRecommendationCurrent()
    {
        if (!_completedPick || _recommendation is null || !_context.IsAvailable ||
            DateTime.UtcNow < _scanTime ||
            DateTime.UtcNow - _scanTime > TimeSpan.FromMinutes(15) ||
            _scanNetworkKey != _context.Key || _scanSuite != StrategyTestHistory.CurrentProbeSuiteFingerprint()) return false;
        return _state.Strategies.Any(s => s.Name == _recommendation.Strategy.Name &&
                                         StrategyTestHistory.Fingerprint(s) == StrategyTestHistory.Fingerprint(_recommendation.Strategy));
    }

    private async Task ApplyRecommendationAsync(CancellationToken ct)
    {
        UpdateContext(NetworkContext.Capture());
        if (!IsRecommendationCurrent())
            throw new InvalidOperationException("Результат устарел — повторите подбор");
        var trial = _recommendation!;
        StepText = "3 / 3 · Запуск";
        StatusText = await _state.ApplyAssistantStrategyAsync(trial.Strategy, trial.Mode, ct)
            ? "Стратегия запущена" : "Стратегия не запустилась — откройте журнал";
    }

    private Strategy? FindProfileStrategy(ConnectionProfile? profile)
        => profile is null ? null : _state.Strategies.FirstOrDefault(s => ConnectionProfileStore.IsCompatible(profile, _context, s));

    private async Task ApplyProfileAsync(CancellationToken ct)
    {
        var profile = SelectedProfile;
        UpdateContext(NetworkContext.Capture());
        var strategy = FindProfileStrategy(profile);
        if (strategy is null)
            throw new InvalidOperationException("Профиль не подходит этой сети или обновился — повторите подбор");
        StepText = "3 / 3 · Профиль";
        StatusText = await _state.ApplyAssistantStrategyAsync(strategy, profile!.Mode, ct)
            ? $"Профиль «{profile.Name}» запущен" : "Профиль не запустился — откройте журнал";
    }

    private void SaveProfile()
    {
        UpdateContext(NetworkContext.Capture());
        if (!CanSaveProfile) { StatusText = "Повторите подбор для текущей сети"; return; }
        var trial = _recommendation!;
        var profile = new ConnectionProfile
        {
            Name = ProfileNameText.Trim(), NetworkKey = _context.Key,
            StrategyName = trial.Strategy.Name, StrategyFingerprint = StrategyTestHistory.Fingerprint(trial.Strategy),
            Mode = trial.Mode, Goal = Goal, ProbeSuiteFingerprint = _scanSuite, SavedAtUtc = DateTime.UtcNow
        };
        if (!_store.Save(profile)) { StatusText = "Не удалось сохранить профиль"; return; }
        UpdateContext(_context);
        StatusText = "Профиль сохранён";
    }

    private void RemoveProfile()
    {
        if (IsBusy || SelectedProfile is null) return;
        if (!_store.Remove(SelectedProfile)) { StatusText = "Не удалось удалить профиль"; return; }
        UpdateContext(_context);
        StatusText = "Профиль удалён";
    }

    private void Cancel()
    {
        if (!CanCancel) return;
        _cancellation?.Cancel();
        if (_state.Tester.IsRunning) _state.Tester.Cancel();
        StatusText = "Завершаем проверку…";
        RefreshActions();
    }

    private void OnTesterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsBusy || !_state.Tester.IsRunning) return;
        if (e.PropertyName == nameof(StrategyTester.Progress)) Progress = 0.15 + _state.Tester.Progress * 0.8;
        if (e.PropertyName == nameof(StrategyTester.StatusText)) StatusText = _state.Tester.StatusText;
    }

    private static void SetServices(ObservableCollection<ConnectionServiceResult> target, IReadOnlyList<ProbeResult>? probes)
    {
        target.Clear();
        foreach (var result in ConnectionAssessment.ServiceResults(probes)) target.Add(result);
    }
}

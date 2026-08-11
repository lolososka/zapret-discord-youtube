using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace ZapretGui.Core;

/// <summary>Результат одной попытки: сколько проб прошло и с какой задержкой.</summary>
public sealed record StrategyTrial(Strategy Strategy, bool Success, int OkCount, int TotalCount,
                                   int AverageLatencyMs, string Detail, DateTime TestedAtUtc,
                                   GameFilterMode Mode)
{
    public string Title => Strategy?.DisplayName ?? "—";

    public string ScoreText => $"{OkCount} из {TotalCount}";

    public string LatencyText => AverageLatencyMs > 0 ? AverageLatencyMs + " мс" : "—";

    public string TooltipText => string.IsNullOrWhiteSpace(Detail)
        ? $"Проверено {StrategyTestHistory.LocalTimeText(TestedAtUtc)}\n{StrategyTestHistory.ModeText(Mode)}"
        : $"{Detail}\nПроверено {StrategyTestHistory.LocalTimeText(TestedAtUtc)}\n{StrategyTestHistory.ModeText(Mode)}";
}

public sealed class StrategyTestRunStartedEventArgs(
    DateTime startedAtUtc,
    GameFilterMode mode,
    int totalStrategies) : EventArgs
{
    public DateTime StartedAtUtc { get; } = startedAtUtc;
    public GameFilterMode Mode { get; } = mode;
    public int TotalStrategies { get; } = totalStrategies;
}

public sealed class StrategyTrialCompletedEventArgs(StrategyTrial trial) : EventArgs
{
    public StrategyTrial Trial { get; } = trial;
}

public sealed class StrategyTestRunFinishedEventArgs(
    DateTime finishedAtUtc,
    StrategyTestRunStatus status) : EventArgs
{
    public DateTime FinishedAtUtc { get; } = finishedAtUtc;
    public StrategyTestRunStatus Status { get; } = status;
}

/// <summary>
/// Перебор стратегий: запустить → подождать подъёма → прогнать пробы → записать результат.
/// Единственный способ честно узнать, что работает у конкретного провайдера.
/// </summary>
public sealed class StrategyTester : ObservableObject
{
    /// <summary>
    /// StartAsync уже проверяет процесс 2,5 секунды. Здесь оставляем только короткое
    /// окно, чтобы сетевой стек увидел новый фильтр, не замедляя каждую стратегию ещё на 2 секунды.
    /// </summary>
    private const int SettleMs = 500;

    private static readonly Lazy<StrategyTester> LazyInstance =
        new(() => new StrategyTester(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static StrategyTester Instance => LazyInstance.Value;

    private readonly BypassController _bypass = BypassController.Instance;
    private readonly Dispatcher? _dispatcher;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private bool _busy;

    private StrategyTester()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        Results = new ObservableCollection<StrategyTrial>();
    }

    // ---------- состояние ----------

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) RaiseMany(nameof(HasActivity), nameof(CanApplyBest)); }
    }

    private double _progress;
    /// <summary>0..1 — доля пройденных стратегий.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    private string _statusText = "Перебор не запускался";
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public ObservableCollection<StrategyTrial> Results { get; }

    private StrategyTrial? _best;
    public StrategyTrial? Best
    {
        get => _best;
        private set { if (Set(ref _best, value)) { Raise(nameof(BestText)); Raise(nameof(CanApplyBest)); } }
    }

    public string BestText => Best is null
        ? "Пока ничего не подошло"
        : $"{Best.Title} — {Best.ScoreText}, {Best.LatencyText}";

    private bool _hasStoredRun;
    public bool HasStoredRun
    {
        get => _hasStoredRun;
        private set { if (Set(ref _hasStoredRun, value)) Raise(nameof(HasActivity)); }
    }

    public bool HasResults => Results.Count > 0;

    /// <summary>Панель видна во время проверки и после восстановления сохранённого прогона.</summary>
    public bool HasActivity => IsRunning || HasStoredRun || HasResults;

    public bool CanApplyBest => !IsRunning && Best is not null;

    public event EventHandler<StrategyTestRunStartedEventArgs>? RunStarted;
    public event EventHandler<StrategyTrialCompletedEventArgs>? TrialCompleted;
    public event EventHandler<StrategyTestRunFinishedEventArgs>? RunFinished;

    /// <summary>Возвращает в панель последний сохранённый прогон через актуальные объекты Strategy.</summary>
    public void RestoreHistory(
        IReadOnlyList<StrategyTrial> trials,
        StrategyTestRun? run,
        string? unavailableReason = null)
    {
        if (IsRunning)
            return;

        Ui(() =>
        {
            Results.Clear();
            Best = null;
            HasStoredRun = run is not null;

            foreach (var trial in trials ?? Array.Empty<StrategyTrial>())
            {
                Results.Add(trial);
                if (IsBetter(trial, Best))
                    Best = trial;
            }

            if (run is null)
            {
                Progress = 0;
                StatusText = "Перебор не запускался";
            }
            else if (!string.IsNullOrWhiteSpace(unavailableReason))
            {
                Progress = 0;
                StatusText = $"Последний автоподбор — " +
                             $"{StrategyTestHistory.LocalTimeText(run.FinishedAtUtc ?? run.StartedAtUtc)} · " +
                             $"{StrategyTestHistory.ModeText(run.Mode)}. " +
                             unavailableReason;
            }
            else
            {
                Progress = run.Status == StrategyTestRunStatus.Completed
                    ? 1
                    : run.TotalStrategies > 0
                        ? Math.Clamp((double)run.Results.Count / run.TotalStrategies, 0, 1)
                        : 0;
                StatusText = RestoredStatusText(run, Best, Results.Count);
            }

            RaiseMany(nameof(HasActivity), nameof(HasResults), nameof(CanApplyBest), nameof(BestText));
        });
    }

    // ---------- перебор ----------

    public async Task<StrategyTestRunStatus> RunAsync(
        IEnumerable<Strategy> strategies,
        GameFilterMode mode,
        CancellationToken ct)
    {
        var list = strategies?.Where(s => s is not null).ToList() ?? new List<Strategy>();

        if (list.Count == 0)
        {
            Ui(() => StatusText = "Стратегий не найдено — проверять нечего");
            return StrategyTestRunStatus.Failed;
        }

        lock (_gate)
        {
            if (_busy) return StrategyTestRunStatus.Cancelled;
            _busy = true;
        }

        CancellationTokenSource cts;
        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        catch
        {
            lock (_gate) _busy = false;
            return StrategyTestRunStatus.Failed;
        }

        lock (_gate) _cts = cts;
        var token = cts.Token;
        var startedAtUtc = DateTime.UtcNow;

        Ui(() =>
        {
            Results.Clear();
            Best = null;
            HasStoredRun = false;
            Progress = 0;
            IsRunning = true;
            RaiseMany(nameof(HasActivity), nameof(HasResults));
            StatusText = $"Готовлюсь проверить {list.Count} {Plural(list.Count, "стратегию", "стратегии", "стратегий")}";
            NotifySafely(() => RunStarted?.Invoke(
                this,
                new StrategyTestRunStartedEventArgs(startedAtUtc, mode, list.Count)));
        });

        int total = list.Count;
        bool cancelled = false;
        bool failed = false;
        var finishStatus = StrategyTestRunStatus.Failed;

        try
        {
            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();

                var strategy = list[i];
                int step = i + 1;

                Ui(() => StatusText = $"Проверяю {strategy.DisplayName} — {step} из {total}");

                var trial = await TryOneAsync(strategy, mode, token).ConfigureAwait(false);

                Ui(() =>
                {
                    Results.Add(trial);
                    RaiseMany(nameof(HasActivity), nameof(HasResults));

                    if (IsBetter(trial, Best))
                        Best = trial;

                    Progress = (double)step / total;
                    NotifySafely(() => TrialCompleted?.Invoke(
                        this,
                        new StrategyTrialCompletedEventArgs(trial)));
                });
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failed = true;
            _bypass.Log("Автоподбор прерван ошибкой: " + ex.Message, LogLevel.Error);
        }
        finally
        {
            // Процесс не должен пережить перебор ни при отмене, ни при ошибке.
            try { await _bypass.StopAsync().ConfigureAwait(false); } catch { }

            lock (_gate)
            {
                _cts = null;
                _busy = false;
            }
            try { cts.Dispose(); } catch { }

            var best = Best;
            finishStatus = failed
                ? StrategyTestRunStatus.Failed
                : cancelled
                    ? StrategyTestRunStatus.Cancelled
                    : StrategyTestRunStatus.Completed;
            var finishedAtUtc = DateTime.UtcNow;
            Ui(() =>
            {
                IsRunning = false;
                HasStoredRun = Results.Count > 0;
                Progress = finishStatus == StrategyTestRunStatus.Completed ? 1 : Progress;
                StatusText = failed
                    ? "Перебор прерван ошибкой" + (best is null ? "" : $". Лучшая пока — {best.Title}")
                    : cancelled
                    ? "Перебор остановлен" + (best is null ? "" : $". Лучшая пока — {best.Title}")
                    : best is null
                        ? "Ни одна стратегия не открыла сайты. Загляните в «Диагностику»"
                        : best.Success
                            ? $"Лучший результат: {best.Title} — {best.ScoreText}, {best.LatencyText}"
                            : $"Полностью рабочая стратегия не найдена. Лучший частичный результат: {best.Title} — {best.ScoreText}";
                RaiseMany(nameof(HasActivity), nameof(HasResults), nameof(CanApplyBest), nameof(BestText));
                NotifySafely(() => RunFinished?.Invoke(
                    this,
                    new StrategyTestRunFinishedEventArgs(finishedAtUtc, finishStatus)));
            });
        }

        return finishStatus;
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _cts;

        try
        {
            if (cts is null || cts.IsCancellationRequested)
                return;
            Ui(() => StatusText = "Останавливаю автоподбор…");
            cts.Cancel();
        }
        catch { /* уже освобождён */ }
    }

    // ---------- одна попытка ----------

    private async Task<StrategyTrial> TryOneAsync(Strategy strategy, GameFilterMode mode, CancellationToken token)
    {
        var sites = ConnectivityTester.ScoredSites;
        int totalSites = ConnectivityTester.ScoredSiteCount;

        await _bypass.StopAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        bool started = await _bypass.StartAsync(strategy, mode, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!started)
            return new StrategyTrial(strategy, false, 0, totalSites, 0,
                "Не удалось запустить — смотрите журнал", DateTime.UtcNow, mode);

        await Task.Delay(SettleMs, token).ConfigureAwait(false);

        IReadOnlyList<ProbeResult> probes;
        try
        {
            probes = await ConnectivityTester.ProbeAllAsync(sites, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StrategyTrial(strategy, false, 0, totalSites, 0,
                "Проверка сорвалась: " + ex.Message, DateTime.UtcNow, mode);
        }

        token.ThrowIfCancellationRequested();

        var scored = probes.Where(p => p.Site.CountsTowardStrategyScore).ToArray();
        int ok = scored.Count(p => p.Ok);
        int avg = ok > 0 ? (int)Math.Round(scored.Where(p => p.Ok).Average(p => p.LatencyMs)) : 0;
        var failedTargets = scored.Where(p => !p.Ok).ToArray();
        static string FailureText(ProbeResult result) =>
            string.IsNullOrWhiteSpace(result.Error)
                ? result.Site.Name
                : $"{result.Site.Name}: {result.Error}";

        string detail = ok == totalSites
            ? "Discord и YouTube открылись"
            : ok > 0
                ? "Частичный результат. Не открылись: " +
                  string.Join("; ", failedTargets.Select(FailureText))
                : "Discord и YouTube не открылись: " +
                  string.Join("; ", failedTargets.Select(FailureText));

        return new StrategyTrial(strategy, ok == totalSites, ok, totalSites, avg, detail, DateTime.UtcNow, mode);
    }

    /// <summary>Больше успешных проб, при равенстве — меньшая задержка.</summary>
    private static bool IsBetter(StrategyTrial candidate, StrategyTrial? current)
    {
        if (candidate.OkCount == 0) return false;
        if (current is null) return true;
        if (candidate.OkCount != current.OkCount) return candidate.OkCount > current.OkCount;
        return candidate.AverageLatencyMs < current.AverageLatencyMs;
    }

    private static string RestoredStatusText(StrategyTestRun run, StrategyTrial? best, int validResultCount)
    {
        var when = StrategyTestHistory.LocalTimeText(run.FinishedAtUtc ?? run.StartedAtUtc);
        var mode = StrategyTestHistory.ModeText(run.Mode);
        var count = $"проверено {validResultCount} из {run.TotalStrategies}";
        if (validResultCount == 0 && run.Results.Count > 0)
            return $"Последний автоподбор — {when} · {mode}. Результаты больше не соответствуют текущим стратегиям";
        int staleCount = Math.Max(0, run.Results.Count - validResultCount);
        var prefix = run.Status switch
        {
            StrategyTestRunStatus.Completed => $"Последний автоподбор — {when} · {mode}",
            StrategyTestRunStatus.Cancelled => $"Последний автоподбор остановлен — {when}, {count} · {mode}",
            StrategyTestRunStatus.Failed => $"Последний автоподбор завершился ошибкой — {when}, {count} · {mode}",
            _ => $"Предыдущий автоподбор прервался — {when}, {count} · {mode}",
        };

        var result = best is null
            ? prefix + ". Рабочих результатов нет"
            : best.Success
                ? $"{prefix}. Лучшая — {best.Title}, {best.ScoreText}, {best.LatencyText}"
                : $"{prefix}. Полного результата нет; лучший частичный — {best.Title}, {best.ScoreText}";
        return staleCount == 0
            ? result
            : $"{result}. Неактуальных результатов: {staleCount}";
    }

    private void NotifySafely(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            _bypass.Log("Не удалось сохранить состояние автоподбора: " + ex.Message, LogLevel.Warn);
        }
    }

    // ---------- служебное ----------

    /// <summary>BypassController уходит на пул через ConfigureAwait(false) — коллекцию трогаем только из UI.</summary>
    private void Ui(Action action)
    {
        try
        {
            var d = _dispatcher ?? System.Windows.Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess())
            {
                action();
                return;
            }
            d.Invoke(action);
        }
        catch
        {
            // приложение закрывается — обновлять уже нечего
        }
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return many;

        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}

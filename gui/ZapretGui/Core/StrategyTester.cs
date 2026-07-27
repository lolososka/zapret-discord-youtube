using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace ZapretGui.Core;

/// <summary>Результат одной попытки: сколько проб прошло и с какой задержкой.</summary>
public sealed record StrategyTrial(Strategy Strategy, bool Success, int OkCount, int TotalCount,
                                   int AverageLatencyMs, string Detail)
{
    public string Title => Strategy?.DisplayName ?? "—";

    public string ScoreText => $"{OkCount} из {TotalCount}";

    public string LatencyText => AverageLatencyMs > 0 ? AverageLatencyMs + " мс" : "—";
}

/// <summary>
/// Перебор стратегий: запустить → подождать подъёма → прогнать пробы → записать результат.
/// Единственный способ честно узнать, что работает у конкретного провайдера.
/// </summary>
public sealed class StrategyTester : ObservableObject
{
    /// <summary>Пауза после запуска: winws.exe успевает поднять WinDivert и загрузить списки.</summary>
    private const int SettleMs = 2000;

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

    /// <summary>Панель перебора видна, пока идёт проверка или пока есть результаты.</summary>
    public bool HasActivity => IsRunning || Results.Count > 0;

    public bool CanApplyBest => !IsRunning && Best is not null;

    // ---------- перебор ----------

    public async Task RunAsync(IEnumerable<Strategy> strategies, GameFilterMode mode, CancellationToken ct)
    {
        var list = strategies?.Where(s => s is not null).ToList() ?? new List<Strategy>();

        if (list.Count == 0)
        {
            Ui(() => StatusText = "Стратегий не найдено — проверять нечего");
            return;
        }

        lock (_gate)
        {
            if (_busy) return;
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
            return;
        }

        lock (_gate) _cts = cts;
        var token = cts.Token;

        Ui(() =>
        {
            Results.Clear();
            Best = null;
            Progress = 0;
            IsRunning = true;
            Raise(nameof(HasActivity));
            StatusText = $"Готовлюсь проверить {list.Count} {Plural(list.Count, "стратегию", "стратегии", "стратегий")}";
        });

        int total = list.Count;
        bool cancelled = false;

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
                    Raise(nameof(HasActivity));

                    if (IsBetter(trial, Best))
                        Best = trial;

                    Progress = (double)step / total;
                });
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
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
            Ui(() =>
            {
                IsRunning = false;
                Progress = cancelled ? Progress : 1;
                StatusText = cancelled
                    ? "Перебор остановлен" + (best is null ? "" : $". Лучшая пока — {best.Title}")
                    : best is null
                        ? "Ни одна стратегия не открыла сайты. Загляните в «Диагностику»"
                        : $"Лучший результат: {best.Title} — {best.ScoreText}, {best.LatencyText}";
                RaiseMany(nameof(HasActivity), nameof(CanApplyBest), nameof(BestText));
            });
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _cts;

        try { cts?.Cancel(); }
        catch { /* уже освобождён */ }
    }

    // ---------- одна попытка ----------

    private async Task<StrategyTrial> TryOneAsync(Strategy strategy, GameFilterMode mode, CancellationToken token)
    {
        var sites = ConnectivityTester.Sites;
        int totalSites = sites.Count;

        await _bypass.StopAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        bool started = await _bypass.StartAsync(strategy, mode).ConfigureAwait(false);
        if (!started)
            return new StrategyTrial(strategy, false, 0, totalSites, 0, "Не удалось запустить — смотрите журнал");

        await Task.Delay(SettleMs, token).ConfigureAwait(false);

        var probes = new List<ProbeResult>(totalSites);
        try
        {
            var tasks = sites.Select(s => ConnectivityTester.ProbeAsync(s, token)).ToArray();
            probes.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StrategyTrial(strategy, false, 0, totalSites, 0, "Проверка сорвалась: " + ex.Message);
        }

        token.ThrowIfCancellationRequested();

        int ok = probes.Count(p => p.Ok);
        int avg = ok > 0 ? (int)Math.Round(probes.Where(p => p.Ok).Average(p => p.LatencyMs)) : 0;

        string detail = ok == totalSites
            ? "Открылись все проверяемые сайты"
            : ok > 0
                ? "Не открылись: " + string.Join(", ", probes.Where(p => !p.Ok).Select(p => p.Site.Name))
                : "Ни один сайт не открылся";

        return new StrategyTrial(strategy, ok > 0, ok, totalSites, avg, detail);
    }

    /// <summary>Больше успешных проб, при равенстве — меньшая задержка.</summary>
    private static bool IsBetter(StrategyTrial candidate, StrategyTrial? current)
    {
        if (candidate.OkCount == 0) return false;
        if (current is null) return true;
        if (candidate.OkCount != current.OkCount) return candidate.OkCount > current.OkCount;
        return candidate.AverageLatencyMs < current.AverageLatencyMs;
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

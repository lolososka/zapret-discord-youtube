using System.Security.Cryptography;
using System.Text;

namespace ZapretGui.Core;

/// <summary>Чем закончился сохранённый автоподбор.</summary>
public enum StrategyTestRunStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
    Interrupted,
}

/// <summary>Один результат внутри последнего прогона.</summary>
public sealed class StrategyTestResult
{
    public string StrategyName { get; set; } = string.Empty;
    public string StrategyFingerprint { get; set; } = string.Empty;
    public DateTime TestedAtUtc { get; set; }
    public int OkCount { get; set; }
    public int TotalCount { get; set; }
    public int AverageLatencyMs { get; set; }
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Последний прогон хранится как единое целое: результаты разных режимов и дат
/// не смешиваются и потому остаются понятными после перезапуска приложения.
/// </summary>
public sealed class StrategyTestRun
{
    public int SchemaVersion { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public GameFilterMode Mode { get; set; }
    public StrategyTestRunStatus Status { get; set; }
    public int TotalStrategies { get; set; }
    public string ProbeSuiteFingerprint { get; set; } = string.Empty;
    public List<StrategyTestResult> Results { get; set; } = new();
}

/// <summary>Проверка, нормализация и человекочитаемые подписи истории.</summary>
public static class StrategyTestHistory
{
    public const int CurrentSchemaVersion = 2;
    public const int MaxResults = 256;
    public const int MaxDetailLength = 512;
    private const int MaxLatencyMs = 10 * 60 * 1000;

    public static string Fingerprint(Strategy strategy)
    {
        var raw = strategy?.RawCommandLine?.Trim() ?? string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static string CurrentProbeSuiteFingerprint()
    {
        var source = "zapret-probes-v2-direct-strict-tls\n" +
                     string.Join(
                         "\n",
                         ConnectivityTester.ScoredSites.Select(
                             site => $"{site.Name}\t{site.Url}\t{site.CountsTowardStrategyScore}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    public static bool UsesCurrentProbeSuite(StrategyTestRun run)
        => run is not null
           && string.Equals(
               run.ProbeSuiteFingerprint,
               CurrentProbeSuiteFingerprint(),
               StringComparison.OrdinalIgnoreCase);

    public static bool Matches(Strategy strategy, StrategyTestResult result)
        => strategy is not null
           && result is not null
           && string.Equals(strategy.Name, result.StrategyName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Fingerprint(strategy), result.StrategyFingerprint, StringComparison.OrdinalIgnoreCase);

    public static string ModeText(GameFilterMode mode) => mode switch
    {
        GameFilterMode.All => "игровой фильтр TCP и UDP",
        GameFilterMode.Tcp => "игровой фильтр только TCP",
        GameFilterMode.Udp => "игровой фильтр только UDP",
        _ => "игровой фильтр выключен",
    };

    public static string LocalTimeText(DateTime utc)
    {
        var local = EnsureUtc(utc).ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today)
            return $"сегодня в {local:HH:mm}";
        if (local.Date == today.AddDays(-1))
            return $"вчера в {local:HH:mm}";
        return local.ToString("dd.MM.yyyy в HH:mm");
    }

    /// <summary>
    /// Недоверенный JSON не должен создавать нелепые счётчики или огромные строки.
    /// Возвращается новая, ограниченная копия; безнадёжно повреждённый прогон отбрасывается.
    /// </summary>
    public static StrategyTestRun? Normalize(StrategyTestRun? source, DateTime? nowUtc = null)
    {
        if (source is null)
            return null;
        var probeFingerprint = source.ProbeSuiteFingerprint?.Trim() ?? string.Empty;
        if (source.SchemaVersion != CurrentSchemaVersion || !IsSha256(probeFingerprint))
            return null;

        var now = EnsureUtc(nowUtc ?? DateTime.UtcNow);
        var started = EnsureUtc(source.StartedAtUtc);
        if (started == default || started > now.AddDays(1))
            return null;
        if (source.TotalStrategies is <= 0 or > MaxResults || !Enum.IsDefined(source.Mode))
            return null;

        var mode = source.Mode;
        var status = Enum.IsDefined(source.Status) ? source.Status : StrategyTestRunStatus.Interrupted;
        if (status == StrategyTestRunStatus.Running)
            status = StrategyTestRunStatus.Interrupted;

        var normalized = new List<StrategyTestResult>();
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourceResults = source.Results ?? new List<StrategyTestResult>();
        foreach (var item in sourceResults)
        {
            var clean = NormalizeResult(item, started, now);
            if (clean is null)
                continue;

            if (indexByName.TryGetValue(clean.StrategyName, out int existing))
            {
                normalized[existing] = clean;
                continue;
            }

            if (normalized.Count >= MaxResults)
                break;

            indexByName[clean.StrategyName] = normalized.Count;
            normalized.Add(clean);
        }

        DateTime? finished = source.FinishedAtUtc is { } value ? EnsureUtc(value) : null;
        if (finished < started || finished > now.AddDays(1))
            finished = null;

        int total = source.TotalStrategies;
        bool structureChanged = normalized.Count != sourceResults.Count || normalized.Count > total;
        if (normalized.Count > total)
            normalized = normalized.Take(total).ToList();

        if (string.Equals(
                probeFingerprint,
                CurrentProbeSuiteFingerprint(),
                StringComparison.OrdinalIgnoreCase))
        {
            int before = normalized.Count;
            normalized.RemoveAll(item => item.TotalCount != ConnectivityTester.ScoredSiteCount);
            structureChanged |= before != normalized.Count;
        }
        else if (normalized.Select(item => item.TotalCount).Distinct().Skip(1).Any())
        {
            structureChanged = true;
        }

        if (finished is { } finishedAt)
        {
            int before = normalized.Count;
            normalized.RemoveAll(item => item.TestedAtUtc > finishedAt.AddMinutes(1));
            structureChanged |= before != normalized.Count;
        }

        if ((status is StrategyTestRunStatus.Cancelled or StrategyTestRunStatus.Failed) && finished is null)
            status = StrategyTestRunStatus.Interrupted;
        if (structureChanged &&
            status is StrategyTestRunStatus.Completed or
                      StrategyTestRunStatus.Cancelled or
                      StrategyTestRunStatus.Failed)
            status = StrategyTestRunStatus.Interrupted;
        if (status == StrategyTestRunStatus.Completed &&
            (finished is null || normalized.Count != total || structureChanged))
            status = StrategyTestRunStatus.Interrupted;
        return new StrategyTestRun
        {
            SchemaVersion = CurrentSchemaVersion,
            StartedAtUtc = started,
            FinishedAtUtc = finished,
            Mode = mode,
            Status = status,
            TotalStrategies = total,
            ProbeSuiteFingerprint = probeFingerprint.ToUpperInvariant(),
            Results = normalized,
        };
    }

    private static StrategyTestResult? NormalizeResult(
        StrategyTestResult? source,
        DateTime runStartedUtc,
        DateTime nowUtc)
    {
        if (source is null)
            return null;

        var name = source.StrategyName?.Trim() ?? string.Empty;
        var fingerprint = source.StrategyFingerprint?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 260 || !IsSha256(fingerprint))
            return null;
        if (source.TotalCount is <= 0 or > 32 ||
            source.OkCount < 0 || source.OkCount > source.TotalCount ||
            source.AverageLatencyMs is < 0 or > MaxLatencyMs)
            return null;

        var tested = EnsureUtc(source.TestedAtUtc);
        if (tested == default || tested < runStartedUtc.AddMinutes(-1) || tested > nowUtc.AddDays(1))
            return null;

        var detail = source.Detail?.Trim() ?? string.Empty;
        if (detail.Length > MaxDetailLength)
            detail = detail[..MaxDetailLength];

        return new StrategyTestResult
        {
            StrategyName = name,
            StrategyFingerprint = fingerprint.ToUpperInvariant(),
            TestedAtUtc = tested,
            OkCount = source.OkCount,
            TotalCount = source.TotalCount,
            AverageLatencyMs = source.AverageLatencyMs,
            Detail = detail,
        };
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (char c in value)
            if (!char.IsAsciiHexDigit(c))
                return false;

        return true;
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

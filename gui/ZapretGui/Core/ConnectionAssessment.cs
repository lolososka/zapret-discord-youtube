namespace ZapretGui.Core;

public enum ConnectionGoal
{
    All,
    YouTube,
    Discord,
}

/// <summary>Честная сводка HTTPS-проб, а не гарантия работы всего сервиса.</summary>
public sealed record ConnectionServiceResult(
    string Name,
    string StatusText,
    string LatencyText,
    string Detail,
    bool IsOk,
    bool HasResult);

/// <summary>Чистые преобразования результатов: без сети, процесса или состояния приложения.</summary>
public static class ConnectionAssessment
{
    private static readonly string[] YouTubeNames = { "YouTube", "YouTube CDN" };
    private static readonly string[] DiscordNames = { "Discord API", "Discord CDN", "Discord Media" };
    private static readonly string[] InternetNames = { "Google" };
    private static readonly HashSet<string> ScoredNames = ConnectivityTester.ScoredSites
        .Select(site => site.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KnownNames = ConnectivityTester.Sites
        .Select(site => site.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Сначала успешные пробы выбранной цели, затем остальных целей и задержка.
    /// Контроль Google и региональный видеохост не влияют на подбор стратегии.
    /// История без отдельных проб пригодна только для общего рейтинга.
    /// </summary>
    public static StrategyTrial? Recommended(IEnumerable<StrategyTrial> trials, ConnectionGoal goal)
    {
        if (trials is null || !Enum.IsDefined(goal))
            return null;

        StrategyTrial? best = null;
        TrialRank bestRank = default;
        foreach (var trial in trials)
        {
            if (trial is null)
                continue;

            var rank = Rank(trial, goal);
            if (rank.RelevantSuccesses <= 0)
                continue;
            if (best is null || rank.RelevantSuccesses > bestRank.RelevantSuccesses ||
                (rank.RelevantSuccesses == bestRank.RelevantSuccesses &&
                 (rank.OtherSuccesses > bestRank.OtherSuccesses ||
                  (rank.OtherSuccesses == bestRank.OtherSuccesses && rank.LatencyMs < bestRank.LatencyMs))))
            {
                best = trial;
                bestRank = rank;
            }
        }
        return best;
    }

    public static IReadOnlyList<ConnectionServiceResult> ServiceResults(IReadOnlyList<ProbeResult>? probes)
    {
        var results = KnownResults(probes);
        return new[]
        {
            ServiceResult("YouTube", YouTubeNames, results,
                "Проверка охватывает только HTTPS-узлы; воспроизведение видео не проверяется."),
            ServiceResult("Discord", DiscordNames, results,
                "Проверка охватывает только HTTPS API и CDN; голосовая связь по UDP не проверяется."),
            ServiceResult("Интернет", InternetNames, results,
                "Google — контроль HTTPS-подключения, а не проверка всех сайтов."),
        };
    }

    private static TrialRank Rank(StrategyTrial trial, ConnectionGoal goal)
    {
        if (trial.Probes is null)
            return goal == ConnectionGoal.All && trial.OkCount > 0 && trial.OkCount <= trial.TotalCount
                ? new TrialRank(trial.OkCount, 0, trial.AverageLatencyMs > 0 ? trial.AverageLatencyMs : int.MaxValue)
                : default;

        var results = KnownResults(trial.Probes).Values
            .Where(probe => probe.Ok && ScoredNames.Contains(probe.Site.Name)).ToArray();
        bool Relevant(ProbeResult probe) => goal switch
        {
            ConnectionGoal.YouTube => YouTubeNames.Contains(probe.Site.Name, StringComparer.OrdinalIgnoreCase),
            ConnectionGoal.Discord => DiscordNames.Contains(probe.Site.Name, StringComparer.OrdinalIgnoreCase),
            _ => true,
        };
        var relevant = results.Where(Relevant).ToArray();
        var latencySamples = relevant.Where(probe => probe.LatencyMs is >= 0 and <= 600_000).ToArray();
        int latency = latencySamples.Length > 0
            ? (int)Math.Round(latencySamples.Average(probe => probe.LatencyMs))
            : int.MaxValue;
        return new TrialRank(relevant.Length, results.Length - relevant.Length, latency);
    }

    private static Dictionary<string, ProbeResult> KnownResults(IReadOnlyList<ProbeResult>? probes)
    {
        var results = new Dictionary<string, ProbeResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in probes ?? Array.Empty<ProbeResult>())
            if (probe?.Site?.Name is { } name && KnownNames.Contains(name))
                results.TryAdd(name, probe);
        return results;
    }

    private static ConnectionServiceResult ServiceResult(
        string name,
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, ProbeResult> results,
        string scope)
    {
        var tested = names.Where(results.ContainsKey).Select(target => results[target]).ToArray();
        var successful = tested.Where(probe => probe.Ok).ToArray();
        bool hasResult = tested.Length > 0;
        bool isOk = tested.Length == names.Count && successful.Length == names.Count;
        string status = !hasResult ? "Не проверен"
            : isOk ? "Доступен по HTTPS"
            : successful.Length == 0 ? "Не открыт по HTTPS"
            : tested.Length < names.Count && successful.Length == tested.Length ? "Проверен частично"
            : "Частично доступен";
        var latencySamples = successful.Where(probe => probe.LatencyMs is >= 0 and <= 600_000).ToArray();
        string latency = latencySamples.Length == 0 ? "—"
            : $"{(int)Math.Round(latencySamples.Average(probe => probe.LatencyMs))} мс";
        var detail = new List<string>();
        if (!hasResult)
            detail.Add("HTTPS-пробы ещё не выполнялись.");
        else
            detail.Add($"Успешных HTTPS-проб: {successful.Length} из {names.Count}.");

        var missing = names.Where(target => !results.ContainsKey(target)).ToArray();
        if (hasResult && missing.Length > 0)
            detail.Add("Не проверено: " + string.Join(", ", missing) + ".");
        var failed = tested.Where(probe => !probe.Ok).Select(probe =>
            string.IsNullOrWhiteSpace(probe.Error) ? probe.Site.Name
                : probe.Site.Name + ": " + ShortError(probe.Error)).ToArray();
        if (failed.Length > 0)
            detail.Add("Не открылись: " + string.Join("; ", failed) + ".");
        detail.Add(scope);
        return new ConnectionServiceResult(name, status, latency, string.Join(" ", detail), isOk, hasResult);
    }

    private static string ShortError(string error)
    {
        var raw = error.Length > StrategyTestHistory.MaxProbeErrorLength
            ? error[..StrategyTestHistory.MaxProbeErrorLength]
            : error;
        return new string(raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
    }

    private readonly record struct TrialRank(int RelevantSuccesses, int OtherSuccesses, int LatencyMs);
}

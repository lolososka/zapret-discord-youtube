using System.Text.Json;
using ZapretGui.Core;

namespace ZapretGui.Smoke;

/// <summary>Pure synthetic checks: no requests, app initialization, bypass, service or driver.</summary>
internal static class ConnectionAssessmentSmoke
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition)
                throw new InvalidOperationException(description);
            checks++;
        }

        var empty = ConnectionAssessment.ServiceResults(null);
        Check(empty.Select(card => card.Name).SequenceEqual(new[] { "YouTube", "Discord", "Интернет" }),
            "The assistant must always expose its two service cards and Internet control.");
        Check(empty.All(card => !card.HasResult && !card.IsOk && card.LatencyText == "—"),
            "Missing data must remain untested, not look like blocked or working services.");

        var allOk = ConnectivityTester.Sites.Select((site, index) => new ProbeResult(site, true, (index + 1) * 10, null)).ToArray();
        var full = ConnectionAssessment.ServiceResults(allOk);
        Check(full.All(card => card.HasResult && card.IsOk), "Complete successful HTTPS groups must be marked available.");
        Check(full[0].LatencyText == "15 мс" && full[1].LatencyText == "40 мс" && full[2].LatencyText == "60 мс",
            "Service latency must average only that service's successful probes.");
        Check(full[0].Detail.Contains("воспроизведение видео не проверяется", StringComparison.Ordinal) &&
              full[1].Detail.Contains("голосовая связь по UDP не проверяется", StringComparison.Ordinal),
            "HTTPS success must never promise YouTube playback or Discord UDP voice.");
        Check(full[2].Detail.Contains("контроль HTTPS", StringComparison.Ordinal),
            "The Internet card must describe Google as a control, not claim every website works.");

        var mixed = allOk.Select(probe => probe.Site.Name == "Discord API"
            ? probe with { Ok = false, LatencyMs = 6000, Error = "timeout" } : probe).ToArray();
        var mixedCards = ConnectionAssessment.ServiceResults(mixed);
        Check(mixedCards[0].IsOk && mixedCards[2].IsOk && !mixedCards[1].IsOk && mixedCards[1].HasResult,
            "A failed Discord endpoint must not alter YouTube or the Internet control.");
        Check(mixedCards[1].StatusText == "Частично доступен" && mixedCards[1].LatencyText == "45 мс" &&
              mixedCards[1].Detail.Contains("Discord API: timeout", StringComparison.Ordinal),
            "Partial groups must retain the failure and exclude its timeout from useful latency.");
        var failed = ConnectionAssessment.ServiceResults(allOk.Select(probe => probe with { Ok = false, Error = "blocked" }).ToArray());
        Check(failed.All(card => card.HasResult && !card.IsOk && card.LatencyText == "—"),
            "Failed probes must not be presented as useful latency or missing data.");
        var partial = ConnectionAssessment.ServiceResults(new[] { Result("YouTube", true, 20) });
        Check(partial[0].HasResult && !partial[0].IsOk && partial[0].Detail.Contains("Не проверено: YouTube CDN", StringComparison.Ordinal),
            "A successful website alone must disclose the untested regional CDN endpoint.");
        Check(!partial[1].HasResult && !partial[2].HasResult, "Absent groups must remain untested in partial results.");
        var unknown = new ProbeResult(new SiteProbe("Unknown", "https://example.invalid/"), true, 1, null);
        Check(ConnectionAssessment.ServiceResults(new[] { unknown }).All(card => !card.HasResult),
            "Unknown probe names must not populate any service card.");

        var youtube = Trial("youtube", new[] { Result("YouTube", true, 100), Result("Discord API", false, 6000) });
        var discord = Trial("discord", new[] { Result("YouTube", false, 6000), Result("Discord API", true, 30),
            Result("Discord CDN", true, 40), Result("Discord Media", true, 50) });
        var candidates = new[] { discord, youtube };
        Check(ConnectionAssessment.Recommended(candidates, ConnectionGoal.YouTube) == youtube,
            "YouTube focus must prioritize a YouTube success over more Discord successes.");
        Check(ConnectionAssessment.Recommended(candidates, ConnectionGoal.Discord) == discord,
            "Discord focus must prioritize its own successful HTTPS endpoints.");
        Check(ConnectionAssessment.Recommended(candidates, ConnectionGoal.All) == discord,
            "All-services focus must prioritize the total scored successes.");
        Check(ConnectionAssessment.Recommended(new[] { discord }, ConnectionGoal.YouTube) is null,
            "Focused ranking must not recommend a trial with zero success for the selected goal.");
        Check(ConnectionAssessment.Recommended(Array.Empty<StrategyTrial>(), ConnectionGoal.All) is null &&
              ConnectionAssessment.Recommended(candidates, (ConnectionGoal)999) is null,
            "Empty input and unknown goals must produce no recommendation.");

        var legacy = new StrategyTrial(NewStrategy("legacy"), true, 4, 4, 10, "old result", DateTime.UtcNow, GameFilterMode.All);
        Check(ConnectionAssessment.Recommended(new[] { legacy, discord }, ConnectionGoal.All) == legacy,
            "Existing aggregate-only results must retain their general recommendation compatibility.");
        Check(ConnectionAssessment.Recommended(new[] { legacy }, ConnectionGoal.YouTube) is null &&
              ConnectionAssessment.Recommended(new[] { legacy }, ConnectionGoal.Discord) is null,
            "Aggregate-only history must not invent per-service successes.");
        Check(ConnectionAssessment.ServiceResults(legacy.Probes).All(card => !card.HasResult),
            "Aggregate-only history must display missing detailed results honestly.");

        var tubeOnlyFast = Trial("tube-fast", new[] { Result("YouTube", true, 1) });
        var fullSlow = Trial("full-slow", ConnectivityTester.ScoredSites.Select(site => new ProbeResult(site, true, 100, null)).ToArray());
        Check(ConnectionAssessment.Recommended(new[] { tubeOnlyFast, fullSlow }, ConnectionGoal.YouTube) == fullSlow,
            "After equal goal success counts, success for the other services must precede latency.");
        var goalFast = Trial("goal-fast", ConnectivityTester.ScoredSites.Select(site =>
            new ProbeResult(site, true, site.Name == "YouTube" ? 10 : 500, null)).ToArray());
        var othersFast = Trial("others-fast", ConnectivityTester.ScoredSites.Select(site =>
            new ProbeResult(site, true, site.Name == "YouTube" ? 20 : 1, null)).ToArray());
        Check(ConnectionAssessment.Recommended(new[] { othersFast, goalFast }, ConnectionGoal.YouTube) == goalFast,
            "The final focused tie-break must use the selected service latency, not unrelated latency.");
        Check(ConnectionAssessment.Recommended(new[] { goalFast, othersFast }, ConnectionGoal.All) == othersFast,
            "The final general tie-break must use the successful scored group latency.");
        Check(ConnectionAssessment.Recommended(new[] { goalFast, goalFast with { Strategy = NewStrategy("tie") } }, ConnectionGoal.All) == goalFast,
            "Exact ties must retain deterministic input order.");
        var diagnosticOnly = Trial("control-only", new[] { Result("Google", true, 1), Result("YouTube CDN", true, 1), unknown });
        Check(ConnectionAssessment.Recommended(new[] { diagnosticOnly }, ConnectionGoal.All) is null &&
              ConnectionAssessment.Recommended(new[] { diagnosticOnly }, ConnectionGoal.YouTube) is null,
            "Internet control, regional video CDN and unknown probes must never become strategy scores.");
        var duplicate = Trial("duplicate", new[] { Result("Discord API", true, 10), Result("Discord API", true, 1) });
        Check(ConnectionAssessment.Recommended(new[] { duplicate, discord }, ConnectionGoal.Discord) == discord,
            "Duplicate endpoints must never multiply a strategy's score.");

        var snapshots = StrategyTestHistory.SnapshotProbes(mixed)!;
        var restored = StrategyTestHistory.RestoreProbes(snapshots)!;
        Check(snapshots.Count == ConnectivityTester.Sites.Count && restored.Count == mixed.Length,
            "Known per-probe results must round-trip through concise snapshots.");
        Check(restored.All(probe => ConnectivityTester.Sites.Any(site => ReferenceEquals(site, probe.Site))),
            "History must reconstruct only the exact current fixed SiteProbe objects.");
        Check(restored.Single(probe => probe.Site.Name == "Discord API").Error == "timeout" &&
              restored.Single(probe => probe.Site.Name == "Discord API").LatencyMs == 6000,
            "The concise history must preserve failed endpoint detail without using its latency in cards.");
        Check(StrategyTestHistory.SnapshotProbes(null) is null && StrategyTestHistory.RestoreProbes(null) is null,
            "Missing old history details must not be fabricated as an executed probe batch.");
        var json = JsonSerializer.Serialize(snapshots);
        Check(!json.Contains("https://", StringComparison.Ordinal) && !json.Contains("CountsToward", StringComparison.Ordinal),
            "Probe snapshots must not persist addresses or configurable scoring metadata.");

        var malicious = new List<ProbeSnapshot>
        {
            new() { Name = " youtube ", Ok = false, LatencyMs = 6000, Error = "timeout\r\n" + new string('x', 500) },
            new() { Name = "YOUTUBE", Ok = true, LatencyMs = 1 },
            new() { Name = "Unknown", Ok = true, LatencyMs = 1 },
            new() { Name = "Discord API", Ok = true, LatencyMs = -1 },
            new() { Name = "Discord CDN", Ok = false, LatencyMs = 600_001 },
            new() { Name = "Google", Ok = true, LatencyMs = 20, Error = "contradictory failure" },
            null!,
        };
        var clean = StrategyTestHistory.RestoreProbes(malicious)!;
        Check(clean.Count == 2 && clean[0].Site.Name == "YouTube" && !clean[0].Ok,
            "History must canonicalize known names and reject duplicates, unknowns and impossible latency.");
        Check(clean[0].Error is { Length: <= StrategyTestHistory.MaxProbeErrorLength } error && !error.Any(char.IsControl),
            "Saved failure messages must be bounded and single-line.");
        Check(clean.Single(probe => probe.Site.Name == "Google").Error is null,
            "Successful snapshots must discard contradictory failure strings.");
        Check(StrategyTestHistory.RestoreProbes(Enumerable.Repeat(new ProbeSnapshot { Name = "Unknown" }, 100_000))!.Count == 0,
            "Malformed probe lists must be bounded during normalization.");

        var started = DateTime.UtcNow.AddMinutes(-5);
        var strategy = NewStrategy("saved");
        var source = new StrategyTestRun
        {
            SchemaVersion = StrategyTestHistory.CurrentSchemaVersion,
            StartedAtUtc = started,
            FinishedAtUtc = started.AddMinutes(1),
            Status = StrategyTestRunStatus.Cancelled,
            Mode = GameFilterMode.All,
            TotalStrategies = 3,
            ProbeSuiteFingerprint = StrategyTestHistory.CurrentProbeSuiteFingerprint(),
            Results = new()
            {
                new StrategyTestResult
                {
                    StrategyName = strategy.Name,
                    StrategyFingerprint = StrategyTestHistory.Fingerprint(strategy),
                    TestedAtUtc = started.AddSeconds(20),
                    OkCount = 3,
                    TotalCount = ConnectivityTester.ScoredSiteCount,
                    AverageLatencyMs = 37,
                    Detail = "partial cancellation history",
                    Probes = StrategyTestHistory.SnapshotProbes(mixed),
                },
            },
        };
        var normalized = StrategyTestHistory.Normalize(source)!;
        Check(normalized.Status == StrategyTestRunStatus.Cancelled && normalized.Results[0].Probes?.Count == 6,
            "Cancellation must retain completed detailed trials, not mark them as failed or incomplete probe data.");
        Check(!ReferenceEquals(normalized.Results[0].Probes, source.Results[0].Probes) &&
              !ReferenceEquals(normalized.Results[0].Probes![0], source.Results[0].Probes![0]),
            "History normalization must deep-copy each saved probe.");
        var preferences = new StrategyPreferences { LastTestRun = normalized };
        var current = preferences.CreateCurrentTrials(new[] { strategy });
        Check(current.Count == 1 && current[0].Probes?.Count == 6 && current[0].Mode == GameFilterMode.All,
            "The preference restore path must preserve detailed probes and the original test mode.");
        Check(ConnectionAssessment.Recommended(current, ConnectionGoal.YouTube) == current[0],
            "Goal-specific ranking must remain available after valid cancelled history restoration.");
        var roundTrip = StrategyPreferences.FromJson(preferences.ToJson());
        Check(roundTrip.LastTestRun?.Status == StrategyTestRunStatus.Cancelled &&
              roundTrip.CreateCurrentTrials(new[] { strategy }).Single().Probes?.Count == 6,
            "The actual preferences JSON round-trip must preserve cancelled run details and restore current trial probes.");
        Check(!preferences.ToJson().Contains("https://", StringComparison.Ordinal),
            "Persisted per-site results must not add URLs to the preferences JSON.");
        source.Results[0].OkCount = 0;
        Check(StrategyTestHistory.Normalize(source)!.Results[0].Probes is null,
            "Detailed successes inconsistent with the saved summary must not create focused recommendations.");
        source.Results[0].Probes = null;
        Check(StrategyTestHistory.Normalize(source)!.Results[0].Probes is null,
            "Old aggregate-only history must retain its missing probe data through normalization.");
        source.Status = StrategyTestRunStatus.Running;
        source.FinishedAtUtc = null;
        Check(StrategyTestHistory.Normalize(source)!.Status == StrategyTestRunStatus.Interrupted,
            "An abandoned running record must still restore as interrupted.");

        return checks;
    }

    private static Strategy NewStrategy(string name) => new()
    {
        Name = name,
        DisplayName = name,
        RawCommandLine = "--test-name=" + name,
    };

    private static ProbeResult Result(string name, bool ok, int latency)
        => new(ConnectivityTester.Sites.Single(site => site.Name == name), ok, latency, ok ? null : "blocked");

    private static StrategyTrial Trial(string name, IReadOnlyList<ProbeResult> probes)
    {
        var scored = probes.Where(probe => probe.Site.CountsTowardStrategyScore).ToArray();
        int successes = scored.Count(probe => probe.Ok);
        int latency = successes == 0 ? 0 : (int)Math.Round(scored.Where(probe => probe.Ok).Average(probe => probe.LatencyMs));
        return new StrategyTrial(NewStrategy(name), successes == ConnectivityTester.ScoredSiteCount,
            successes, ConnectivityTester.ScoredSiteCount, latency, "synthetic", DateTime.UtcNow, GameFilterMode.All, probes);
    }
}

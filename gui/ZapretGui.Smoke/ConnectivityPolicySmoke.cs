using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ZapretGui.Core;

namespace ZapretGui.Smoke;

internal static class ConnectivityPolicySmoke
{
    // Use a fake transport through the existing private overload: no socket, proxy,
    // bypass process, service, video request or public-Internet dependency is involved.
    private static readonly MethodInfo ProbeMethod = typeof(ConnectivityTester).GetMethod(
        "ProbeAsync", BindingFlags.NonPublic | BindingFlags.Static,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(SiteProbe), typeof(CancellationToken) },
        modifiers: null) ?? throw new InvalidOperationException("Probe transport seam not found.");

    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
        }

        Check(ConnectivityTester.ScoredSiteCount == 4,
            "Only the core Discord and YouTube probes may affect scoring.");
        Check(ConnectivityTester.ScoredSites.All(site => site.CountsTowardStrategyScore),
            "The strategy batch must contain only scored targets.");
        Check(ConnectivityTester.Sites.Count(site => !site.CountsTowardStrategyScore) == 2,
            "Regional video CDN and general-Internet control must stay diagnostic-only.");

        foreach (var site in ConnectivityTester.Sites)
        {
            foreach (int code in new[] { 200, 204, 206, 301, 302, 304, 307, 308, 400, 401, 403, 404, 407, 429, 451, 500, 503, 511 })
            {
                var status = (HttpStatusCode)code;
                bool expected = code is >= 200 and < 300 ||
                                site.ResponsePolicy == ProbeResponsePolicy.CdnRoot && code is 403 or 404;
                Check(ConnectivityTester.IsUsableStatus(site, status) == expected,
                    $"{site.Name} must apply its own status policy to HTTP {code}.");
                Check(ConnectivityTester.IsUsableStatus(status) == (code is >= 200 and < 300),
                    $"The default content policy must reject non-2xx HTTP {code}.");

                using var transport = new ReplyHandler(status);
                using var http = new HttpClient(transport);
                var result = await ProbeWithFakeTransportAsync(http, site);
                Check(result.Ok == expected && (result.Error is null) == expected,
                    $"The actual {site.Name} probe must use its policy for HTTP {code}.");
                Check(transport.Calls == 1,
                    "HTTP failures must not be retried as transient connection failures.");
                if (!expected)
                    Check(result.Error?.Contains(code.ToString(), StringComparison.Ordinal) == true,
                        "Rejected HTTP responses must retain their exact status for diagnosis.");
            }
        }

        var page = ConnectivityTester.ScoredSites.Single(site => site.Name == "YouTube");
        var api = ConnectivityTester.ScoredSites.Single(site => site.Name == "Discord API");
        Check(page.ResponsePolicy == ProbeResponsePolicy.Content && api.ResponsePolicy == ProbeResponsePolicy.Content,
            "YouTube pages and Discord API must never inherit CDN-root allowances.");
        Check(ConnectivityTester.ScoredSites.Count(site => ConnectivityTester.IsUsableStatus(site, HttpStatusCode.Forbidden)) == 2,
            "403 for all targets must yield only CDN-root reachability, never a false 4/4.");
        Check(ConnectivityTester.ScoredSites.All(site => !ConnectivityTester.IsUsableStatus(site, HttpStatusCode.TooManyRequests)),
            "Rate-limited responses must not make a strategy look fully working.");

        var createHandler = typeof(ConnectivityTester).GetMethod(
            "CreateHandler", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Probe handler factory not found.");
        using (var handler = (SocketsHttpHandler)createHandler.Invoke(null, null)!)
        {
            Check(handler.AllowAutoRedirect && handler.MaxAutomaticRedirections == 5,
                "Normal HTTPS redirects must resolve before scoring, with a bounded chain.");
            Check(!handler.UseProxy && !handler.UseCookies,
                "Probe redirects must retain the direct, cookie-free route.");
            Check(handler.SslOptions.RemoteCertificateValidationCallback is null,
                "Redirected HTTPS must retain normal certificate validation.");
            // .NET 8 SocketsHttpHandler refuses HTTPS -> HTTP redirects. Inspecting
            // its configured options avoids opening real TLS listeners in smoke tests.
        }

        var previousSource = "zapret-probes-v2-direct-strict-tls\n" + string.Join(
            "\n", ConnectivityTester.ScoredSites.Select(site =>
                $"{site.Name}\t{site.Url}\t{site.CountsTowardStrategyScore}"));
        string previousFingerprint = Hash(previousSource);
        Check(!StrategyTestHistory.UsesCurrentProbeSuite(new StrategyTestRun { ProbeSuiteFingerprint = previousFingerprint }),
            "Old universal-4xx results must not be restored as current strategy scores.");
        var expectedSource = "zapret-probes-v3-direct-strict-tls-bounded-https-redirects\n" + string.Join(
            "\n", ConnectivityTester.ScoredSites.Select(site =>
                $"{site.Name}\t{site.Url}\t{site.CountsTowardStrategyScore}\t{site.ResponsePolicy}"));
        Check(StrategyTestHistory.CurrentProbeSuiteFingerprint() == Hash(expectedSource),
            "The history fingerprint must include each scored target's response policy.");

        var invalid = await ConnectivityTester.ProbeAsync(new SiteProbe("invalid", "http://example.test/"));
        Check(!invalid.Ok && invalid.Error?.Contains("HTTPS", StringComparison.Ordinal) == true,
            "Non-HTTPS probes must fail before any request.");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ExpectCancelledAsync(() => ConnectivityTester.ProbeAsync(page, cancellation.Token));
            checks++;
            await ExpectCancelledAsync(async () =>
                await ConnectivityTester.ProbeAllAsync(ConnectivityTester.Sites, cancellation.Token));
            checks++;
        }

        using (var cancellation = new CancellationTokenSource())
        using (var transport = new CancellationHandler())
        using (var http = new HttpClient(transport))
        {
            var probe = ProbeWithFakeTransportAsync(http, page, cancellation.Token);
            await transport.Started.Task;
            cancellation.Cancel();
            await ExpectCancelledAsync(() => probe);
            checks++;
            Check(transport.Calls == 1,
                "In-flight caller cancellation must propagate without a retry or blocked-site result.");
        }

        var ok = new ProbeResult(page, true, 42, null);
        var failed = new ProbeResult(page, false, 6000, "timeout");
        Check(ok.ResultText == "42 мс" && failed.ResultText == "не открыт",
            "Failed probes must not show timeout as useful latency.");
        return checks;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Task<ProbeResult> ProbeWithFakeTransportAsync(
        HttpClient http, SiteProbe site, CancellationToken cancellation = default)
        => (Task<ProbeResult>)ProbeMethod.Invoke(null, new object[] { http, site, cancellation })!;

    private static async Task ExpectCancelledAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Caller cancellation must propagate, not report site unavailability.");
    }

    private sealed class ReplyHandler(HttpStatusCode status) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Cancellation handler must never return a response.");
        }
    }
}

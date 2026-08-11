using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace ZapretGui.Core;

/// <summary>
/// Сервисные загрузки Flowseal. Проверка версии GUI и установка portable-релиза
/// изолированы в ForkUpdateService и всегда используют строгую проверку TLS.
/// </summary>
public static class UpdateService
{
    public static readonly string LocalVersion =
        VersionPolicy.ProductVersion(typeof(UpdateService).Assembly);

    public const string DownloadUrl = ForkUpdateService.ReleasesUrl;

    private const string RawBase = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        HttpClient client;
        try
        {
            client = new HttpClient(handler, disposeHandler: true);
        }
        catch
        {
            client = new HttpClient();
        }

        client.Timeout = TimeSpan.FromSeconds(20);
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
        }
        catch
        {
            // Заголовки не критичны.
        }

        return client;
    }

    /// <summary>Проверяет последний проверенный portable-релиз community-форка.</summary>
    public static async Task<(bool ok, string? remoteVersion, bool updateAvailable)> CheckAsync(CancellationToken ct = default)
    {
        var (ok, release, updateAvailable) =
            await ForkUpdateService.CheckAsync(ct).ConfigureAwait(false);
        var label = release is null
            ? null
            : $"{release.GuiVersion} · Flowseal {release.UpstreamVersion}";
        return (ok, label, updateAvailable);
    }

    public static string ReleasePageUrl(string version)
        => DownloadUrl;

    /// <summary>Скачивает актуальный ipset-all.txt из репозитория.</summary>
    public static async Task<CommandResult> UpdateIpsetAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var text = await GetStringAsync(RawBase + "ipset-service.txt", cts.Token).ConfigureAwait(false);
            if (text is null)
                return new CommandResult(false, "Не удалось скачать список IP. Проверьте подключение к интернету.");

            var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(static l => l.Trim())
                            .Where(static l => l.Length > 0)
                            .ToArray();

            if (lines.Length == 0)
                return new CommandResult(false, "Сервер вернул пустой список IP — обновление отменено.");

            var target = Path.Combine(AppPaths.Lists, "ipset-all.txt");
            var temp = target + ".download";

            try
            {
                Directory.CreateDirectory(AppPaths.Lists);
                await File.WriteAllTextAsync(temp, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false), cts.Token)
                          .ConfigureAwait(false);

                File.Move(temp, target, overwrite: true);
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                return new CommandResult(false, "Не удалось сохранить lists\\ipset-all.txt: " + ex.Message);
            }

            return new CommandResult(true, $"Список IP обновлён: {lines.Length} записей.");
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "Обновление списка IP отменено.");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, "Ошибка обновления списка IP: " + ex.Message);
        }
    }

    /// <summary>Сверяет системный hosts с эталонным из репозитория (по первой и последней строке).</summary>
    public static async Task<CommandResult> CheckHostsAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            var remoteText = await GetStringAsync(RawBase + "hosts", cts.Token).ConfigureAwait(false);
            if (remoteText is null)
                return new CommandResult(false, "Не удалось скачать эталонный hosts. Проверьте подключение к интернету.");

            var remoteLines = MeaningfulLines(remoteText);
            if (remoteLines.Count == 0)
                return new CommandResult(false, "Эталонный hosts пуст — проверка невозможна.");

            string hostsPath;
            try
            {
                hostsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "drivers", "etc", "hosts");
            }
            catch
            {
                hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
            }

            if (!File.Exists(hostsPath))
                return new CommandResult(false, "Файл hosts не найден: " + hostsPath);

            List<string> localLines;
            try
            {
                var localText = await File.ReadAllTextAsync(hostsPath, cts.Token).ConfigureAwait(false);
                localLines = MeaningfulLines(localText);
            }
            catch (Exception ex)
            {
                return new CommandResult(false, "Не удалось прочитать hosts: " + ex.Message);
            }

            var firstOk = localLines.Contains(remoteLines[0], StringComparer.OrdinalIgnoreCase);
            var lastOk = localLines.Contains(remoteLines[^1], StringComparer.OrdinalIgnoreCase);

            if (firstOk && lastOk)
                return new CommandResult(true, "Файл hosts актуален — записи из репозитория присутствуют.");

            if (!firstOk && !lastOk)
                return new CommandResult(false, "Файл hosts не содержит записей из репозитория. Обновите его вручную с помощью hosts-скрипта.");

            return new CommandResult(false, "Файл hosts заполнен частично — совпала только часть эталонных записей. Рекомендуется обновить его.");
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "Проверка hosts отменена.");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, "Ошибка проверки hosts: " + ex.Message);
        }
    }

    private static async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Игнорируем.
        }
    }

    private static string FirstMeaningfulLine(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length > 0)
                return line;
        }
        return string.Empty;
    }

    private static List<string> MeaningfulLines(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            result.Add(line);
        }
        return result;
    }

}

public sealed record SiteProbe(
    string Name,
    string Url,
    bool CountsTowardStrategyScore = true);

public sealed record ProbeResult(SiteProbe Site, bool Ok, int LatencyMs, string? Error)
{
    public string ResultText => Ok ? $"{LatencyMs} мс" : "не открыт";
}

/// <summary>
/// Проверка доступности ресурсов, которые чинит zapret. Каждая серия использует новые
/// прямые соединения: результат одной стратегии не попадает в следующую из HTTP-пула.
/// </summary>
public static class ConnectivityTester
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private const int MaxAttempts = 2;

    private static readonly SiteProbe[] SitesArray =
    {
        new("YouTube", "https://www.youtube.com/"),
        // Конкретный CDN-узел зависит от региона, поэтому он полезен в диагностике,
        // но не должен сам решать, какая стратегия лучшая.
        new("YouTube CDN", "https://rr1---sn-4g5e6nlz.googlevideo.com/", false),
        new("Discord API", "https://discord.com/api/v9/gateway"),
        new("Discord CDN", "https://cdn.discordapp.com/"),
        new("Discord Media", "https://media.discordapp.net/"),
        // Контроль обычного интернета: Google не является целью zapret.
        new("Google", "https://www.google.com/", false)
    };

    private static readonly SiteProbe[] ScoredSitesArray =
        SitesArray.Where(site => site.CountsTowardStrategyScore).ToArray();

    public static IReadOnlyList<SiteProbe> Sites { get; } = SitesArray;
    public static IReadOnlyList<SiteProbe> ScoredSites { get; } = ScoredSitesArray;
    public static int ScoredSiteCount => ScoredSitesArray.Length;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            UseCookies = false,
            // Проверяем именно прямой маршрут zapret, а не системный VPN/HTTP-прокси.
            UseProxy = false
        };

        var client = new HttpClient(handler, disposeHandler: true);

        client.Timeout = Timeout.InfiniteTimeSpan; // таймаут задаётся через CancellationTokenSource
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        }
        catch
        {
            // Заголовки не критичны.
        }

        return client;
    }

    /// <summary>
    /// Одна серия проверок с собственной транспортной сессией. Это особенно важно
    /// при автоподборе: новая стратегия не наследует соединения предыдущей.
    /// </summary>
    public static async Task<IReadOnlyList<ProbeResult>> ProbeAllAsync(
        IReadOnlyList<SiteProbe> sites,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ct.ThrowIfCancellationRequested();

        using var http = CreateClient();
        var tasks = sites.Select(site => ProbeAsync(http, site, ct)).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static async Task<ProbeResult> ProbeAsync(SiteProbe site, CancellationToken ct = default)
    {
        using var http = CreateClient();
        return await ProbeAsync(http, site, ct).ConfigureAwait(false);
    }

    private static async Task<ProbeResult> ProbeAsync(
        HttpClient http,
        SiteProbe site,
        CancellationToken ct)
    {
        if (site is null)
            return new ProbeResult(new SiteProbe("—", string.Empty), false, 0, "Не задан адрес проверки.");
        if (!Uri.TryCreate(site.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return new ProbeResult(site, false, 0, "Для проверки нужен корректный HTTPS-адрес.");

        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, uri)
                    {
                        Version = HttpVersion.Version20,
                        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                    };
                    req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                    // Заголовков достаточно: тело не скачиваем.
                    using var response = await http.SendAsync(
                        req,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);

                    sw.Stop();
                    var error = ResponseError(response.StatusCode);
                    return new ProbeResult(site, error is null, ElapsedMilliseconds(sw), error);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
                {
                    await Task.Delay(150, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return new ProbeResult(site, false, ElapsedMilliseconds(sw), Describe(ex));
                }
            }

            sw.Stop();
            return new ProbeResult(site, false, ElapsedMilliseconds(sw), "Соединение не установлено.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ProbeResult(site, false, ElapsedMilliseconds(sw), "Сайт не ответил за 6 секунд.");
        }
    }

    /// <summary>HTTP-ответ означает, что сайт действительно пригоден для текущей проверки.</summary>
    public static bool IsUsableStatus(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code >= 200 && code < 500 &&
               statusCode != HttpStatusCode.ProxyAuthenticationRequired &&
               code != 451 &&
               code != 511;
    }

    private static string? ResponseError(HttpStatusCode statusCode)
    {
        if (IsUsableStatus(statusCode))
            return null;

        return statusCode switch
        {
            HttpStatusCode.ProxyAuthenticationRequired => "Системный прокси требует авторизацию (HTTP 407).",
            _ when (int)statusCode == 451 => "Сайт ответил отказом из-за ограничения доступа (HTTP 451).",
            _ when (int)statusCode == 511 => "Сеть требует входа через страницу авторизации (HTTP 511).",
            _ when (int)statusCode >= 500 => $"Сервер временно недоступен (HTTP {(int)statusCode}).",
            _ => $"Неожиданный ответ сайта (HTTP {(int)statusCode}).",
        };
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is System.Security.Authentication.AuthenticationException)
            return false;
        if (ex is HttpRequestException request && request.InnerException is not null)
            return IsTransient(request.InnerException);
        if (ex is System.Net.Sockets.SocketException socket)
            return socket.SocketErrorCode is
                System.Net.Sockets.SocketError.HostNotFound or
                System.Net.Sockets.SocketError.TryAgain or
                System.Net.Sockets.SocketError.ConnectionAborted or
                System.Net.Sockets.SocketError.ConnectionRefused or
                System.Net.Sockets.SocketError.ConnectionReset or
                System.Net.Sockets.SocketError.NetworkDown or
                System.Net.Sockets.SocketError.NetworkUnreachable;
        return ex is HttpRequestException or IOException;
    }

    private static int ElapsedMilliseconds(Stopwatch stopwatch)
        => (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 0, int.MaxValue);

    private static string Describe(Exception ex)
    {
        var e = ex;
        while (e is AggregateException agg && agg.InnerException is not null)
            e = agg.InnerException;

        switch (e)
        {
            case TaskCanceledException:
            case OperationCanceledException:
                return "Сайт не ответил за 6 секунд.";
            case System.Net.Sockets.SocketException se:
                return se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound => "DNS не разрешает имя узла.",
                    System.Net.Sockets.SocketError.ConnectionRefused => "Соединение отклонено узлом.",
                    System.Net.Sockets.SocketError.ConnectionReset => "Соединение было сброшено удалённой стороной.",
                    System.Net.Sockets.SocketError.TimedOut => "Тайм-аут TCP-соединения.",
                    System.Net.Sockets.SocketError.NetworkUnreachable => "Сеть недоступна.",
                    _ => "Сетевая ошибка (код " + (int)se.SocketErrorCode + ")."
                };
            case System.Security.Authentication.AuthenticationException:
                return "Не удалось подтвердить TLS-сертификат сайта.";
            case IOException:
                return "Разрыв соединения при передаче данных.";
        }

        if (e is HttpRequestException hre)
        {
            if (hre.InnerException is not null && hre.InnerException != e)
                return Describe(hre.InnerException);
            return "Нет соединения с узлом.";
        }

        return "Неизвестная сетевая ошибка.";
    }
}

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
    public const string LocalVersion = "1.11.0";

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

    /// <summary>Числовое сравнение версий вида 1.10.0; при разборе не бросает.</summary>
    private static bool IsNewer(string remote, string local)
    {
        try
        {
            var r = ParseVersion(remote);
            var l = ParseVersion(local);
            var len = Math.Max(r.Length, l.Length);
            for (var i = 0; i < len; i++)
            {
                var rv = i < r.Length ? r[i] : 0;
                var lv = i < l.Length ? l[i] : 0;
                if (rv != lv)
                    return rv > lv;
            }
            return false;
        }
        catch
        {
            return !string.Equals(remote.Trim(), local.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int[] ParseVersion(string version)
    {
        var cleaned = new StringBuilder();
        foreach (var c in version)
        {
            if (char.IsDigit(c) || c == '.')
                cleaned.Append(c);
            else if (cleaned.Length > 0 && c != 'v' && c != 'V')
                break;
        }

        return cleaned.ToString()
                      .Split('.', StringSplitOptions.RemoveEmptyEntries)
                      .Select(static p => int.TryParse(p, out var n) ? n : 0)
                      .ToArray();
    }
}

public sealed record SiteProbe(string Name, string Url);

public sealed record ProbeResult(SiteProbe Site, bool Ok, int LatencyMs, string? Error);

/// <summary>
/// Проверка доступности ресурсов, которые чинит zapret. Любой HTTP-код считается успехом —
/// нас интересует только то, что TCP+TLS соединение установилось.
/// </summary>
public static class ConnectivityTester
{
    private static readonly HttpClient Http = CreateClient();

    private static readonly SiteProbe[] SitesArray =
    {
        new("YouTube", "https://www.youtube.com/"),
        new("YouTube CDN", "https://rr1---sn-4g5e6nlz.googlevideo.com/"),
        new("Discord API", "https://discord.com/api/v9/gateway"),
        new("Discord CDN", "https://cdn.discordapp.com/"),
        new("Discord Media", "https://media.discordapp.net/"),
        new("Google", "https://www.google.com/")
    };

    public static IReadOnlyList<SiteProbe> Sites { get; } = SitesArray;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            // При активной блокировке провайдер часто подменяет сертификат; для теста
            // доступности это не важно — важен сам факт установленного соединения.
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
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

    public static async Task<ProbeResult> ProbeAsync(SiteProbe site, CancellationToken ct = default)
    {
        if (site is null)
            return new ProbeResult(new SiteProbe("—", string.Empty), false, 0, "Не задан адрес проверки.");

        var sw = Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            var head = await TrySendAsync(site.Url, HttpMethod.Head, cts.Token).ConfigureAwait(false);
            if (head.ok)
            {
                sw.Stop();
                return new ProbeResult(site, true, (int)sw.ElapsedMilliseconds, null);
            }

            var get = await TrySendAsync(site.Url, HttpMethod.Get, cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (get.ok)
                return new ProbeResult(site, true, (int)sw.ElapsedMilliseconds, null);

            return new ProbeResult(site, false, (int)sw.ElapsedMilliseconds, get.error ?? head.error ?? "Соединение не установлено.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(site, false, (int)sw.ElapsedMilliseconds, Describe(ex));
        }
    }

    private static async Task<(bool ok, string? error)> TrySendAsync(string url, HttpMethod method, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            // Заголовки ответа достаточно — тело качать незачем.
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Любой статус = хост достижим.
            _ = resp.StatusCode;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var e = ex;
        while (e is AggregateException agg && agg.InnerException is not null)
            e = agg.InnerException;

        switch (e)
        {
            case TaskCanceledException:
            case OperationCanceledException:
                return "Превышено время ожидания (6 с) — соединение блокируется.";
            case System.Net.Sockets.SocketException se:
                return se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound => "DNS не разрешает имя узла.",
                    System.Net.Sockets.SocketError.ConnectionRefused => "Соединение отклонено узлом.",
                    System.Net.Sockets.SocketError.ConnectionReset => "Соединение сброшено — типичный признак DPI.",
                    System.Net.Sockets.SocketError.TimedOut => "Тайм-аут TCP-соединения.",
                    System.Net.Sockets.SocketError.NetworkUnreachable => "Сеть недоступна.",
                    _ => "Сетевая ошибка: " + se.SocketErrorCode
                };
            case System.Security.Authentication.AuthenticationException:
                return "Ошибка TLS-рукопожатия — вероятно, вмешательство DPI.";
            case IOException:
                return "Разрыв соединения при передаче данных.";
        }

        if (e is HttpRequestException hre)
        {
            if (hre.InnerException is not null && hre.InnerException != e)
                return Describe(hre.InnerException);
            return "Нет соединения с узлом.";
        }

        var msg = e.Message;
        return string.IsNullOrWhiteSpace(msg) ? "Неизвестная сетевая ошибка." : msg;
    }
}

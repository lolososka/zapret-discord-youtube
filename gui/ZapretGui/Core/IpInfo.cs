using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZapretGui.Core;

/// <summary>Что видит сайт на той стороне: внешний адрес и привязанные к нему данные.</summary>
public sealed record IpDetails(string Ip, string? Country, string? CountryCode,
                               string? City, string? Provider, bool FromCache);

/// <summary>
/// Определение внешнего IP через публичный сервис.
///
/// Запрос раскрывает адрес пользователя стороннему узлу, поэтому вызывается
/// только по явному действию — никаких проверок на старте приложения.
///
/// Обход DPI не влияет на результат: winws.exe правит заголовки пакетов, а маршрут
/// и внешний адрес остаются провайдерскими. Карточка нужна ровно для того, чтобы
/// это было видно, а не для того, чтобы что-то подменить.
/// </summary>
public static class IpInfo
{
    // Общий бюджет запроса и потолок на одну попытку: сервисов два, первый может висеть.
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private const string PrimaryUrl = "https://ipinfo.io/json";
    private const string FallbackUrl = "https://api.myip.com";

    private static readonly HttpClient Http = CreateClient();

    private static readonly object Gate = new();
    private static IpDetails? _cached;
    private static DateTime _cachedAtUtc;

    private static HttpClient CreateClient()
    {
        // Сертификат здесь проверяется штатно, в отличие от ConnectivityTester: там мерили
        // сам факт соединения, а тут читаем данные, которые показываем пользователю как факт.
        HttpClient client;
        try
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            client = new HttpClient(handler, disposeHandler: true);
        }
        catch
        {
            client = new HttpClient();
        }

        // Реальный таймаут задаётся CancellationTokenSource — так он общий на обе попытки.
        client.Timeout = Timeout.InfiniteTimeSpan;

        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretGUI/1.0 (+https://github.com/Flowseal/zapret-discord-youtube)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        }
        catch
        {
            // Заголовки не критичны.
        }

        return client;
    }

    /// <summary>
    /// Возвращает данные о внешнем адресе или <c>null</c>, если ни один сервис не ответил.
    /// Исключений не бросает. Успешный результат живёт в кэше 5 минут.
    /// </summary>
    public static async Task<IpDetails?> LookupAsync(CancellationToken ct = default)
    {
        var cached = TakeCached();
        if (cached is not null)
            return cached;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TotalTimeout);

            var details = await FromPrimaryAsync(cts.Token).ConfigureAwait(false)
                       ?? await FromFallbackAsync(cts.Token).ConfigureAwait(false);

            if (details is null)
                return null;

            Store(details);
            return details;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Сбрасывает кэш — следующий вызов уйдёт в сеть.</summary>
    public static void ClearCache()
    {
        lock (Gate)
        {
            _cached = null;
            _cachedAtUtc = default;
        }
    }

    private static IpDetails? TakeCached()
    {
        lock (Gate)
        {
            if (_cached is null)
                return null;

            if (DateTime.UtcNow - _cachedAtUtc > CacheLifetime)
            {
                _cached = null;
                return null;
            }

            return _cached with { FromCache = true };
        }
    }

    private static void Store(IpDetails details)
    {
        lock (Gate)
        {
            _cached = details with { FromCache = false };
            _cachedAtUtc = DateTime.UtcNow;
        }
    }

    // ipinfo.io: {"ip","city","region","country","org","hostname",...}; country — код ISO.
    private static async Task<IpDetails?> FromPrimaryAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync(PrimaryUrl, ct).ConfigureAwait(false);
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // bogon=true означает частный/служебный адрес — показывать нечего.
            if (ReadBool(root, "bogon"))
                return null;

            var ip = ReadString(root, "ip");
            if (string.IsNullOrEmpty(ip))
                return null;

            var code = NormalizeCode(ReadString(root, "country"));

            return new IpDetails(
                ip,
                CountryName(code),
                code,
                ReadString(root, "city"),
                CleanProvider(ReadString(root, "org")),
                FromCache: false);
        }
        catch
        {
            return null;
        }
    }

    // api.myip.com: {"ip","country","cc"} — без города и провайдера, только как запасной вариант.
    private static async Task<IpDetails?> FromFallbackAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync(FallbackUrl, ct).ConfigureAwait(false);
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var ip = ReadString(root, "ip");
            if (string.IsNullOrEmpty(ip))
                return null;

            var code = NormalizeCode(ReadString(root, "cc"));

            // Русское название предпочтительнее английского из ответа сервиса.
            var country = CountryName(code) ?? ReadString(root, "country");

            return new IpDetails(ip, country, code, City: null, Provider: null, FromCache: false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(AttemptTimeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, attempt.Token)
                                       .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return null;

            var text = await resp.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);

            // При вмешательстве DPI вместо JSON нередко приходит HTML-заглушка.
            var trimmed = text?.Trim().TrimStart('\uFEFF').TrimStart();
            if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{')
                return null;

            return trimmed;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };

        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var code = raw.Trim().ToUpperInvariant();
        if (code.Length != 2)
            return null;

        foreach (var c in code)
        {
            if (c is < 'A' or > 'Z')
                return null;
        }

        return code;
    }

    /// <summary>«AS12389 PJSC Rostelecom» → «PJSC Rostelecom»: номер AS в карточке лишний.</summary>
    private static string? CleanProvider(string? org)
    {
        if (string.IsNullOrWhiteSpace(org))
            return null;

        var text = org.Trim();
        var space = text.IndexOf(' ');
        if (space > 2 && text.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
        {
            var digits = text.AsSpan(2, space - 2);
            var allDigits = true;
            foreach (var c in digits)
            {
                if (!char.IsDigit(c))
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits)
                text = text[(space + 1)..].Trim();
        }

        return text.Length == 0 ? null : text;
    }

    private static string? CountryName(string? code)
    {
        if (code is null)
            return null;

        if (Names.TryGetValue(code, out var name))
            return name;

        // Для стран вне таблицы отдаём системное название: на русской Windows оно русское.
        try
        {
            var display = new RegionInfo(code).DisplayName;
            if (!string.IsNullOrWhiteSpace(display))
                return display;
        }
        catch
        {
            // Неизвестный региону код — покажем сам код.
        }

        return code;
    }

    // Страны, которые реально встречаются у пользователей обхода DPI и типовых VPN-выходов.
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["RU"] = "Россия",
        ["BY"] = "Беларусь",
        ["UA"] = "Украина",
        ["KZ"] = "Казахстан",
        ["UZ"] = "Узбекистан",
        ["KG"] = "Киргизия",
        ["TJ"] = "Таджикистан",
        ["TM"] = "Туркмения",
        ["AM"] = "Армения",
        ["AZ"] = "Азербайджан",
        ["GE"] = "Грузия",
        ["MD"] = "Молдавия",
        ["EE"] = "Эстония",
        ["LV"] = "Латвия",
        ["LT"] = "Литва",
        ["PL"] = "Польша",
        ["DE"] = "Германия",
        ["NL"] = "Нидерланды",
        ["FR"] = "Франция",
        ["GB"] = "Великобритания",
        ["IE"] = "Ирландия",
        ["ES"] = "Испания",
        ["PT"] = "Португалия",
        ["IT"] = "Италия",
        ["CH"] = "Швейцария",
        ["AT"] = "Австрия",
        ["BE"] = "Бельгия",
        ["LU"] = "Люксембург",
        ["CZ"] = "Чехия",
        ["SK"] = "Словакия",
        ["HU"] = "Венгрия",
        ["RO"] = "Румыния",
        ["BG"] = "Болгария",
        ["RS"] = "Сербия",
        ["HR"] = "Хорватия",
        ["SI"] = "Словения",
        ["GR"] = "Греция",
        ["TR"] = "Турция",
        ["CY"] = "Кипр",
        ["SE"] = "Швеция",
        ["NO"] = "Норвегия",
        ["FI"] = "Финляндия",
        ["DK"] = "Дания",
        ["IS"] = "Исландия",
        ["US"] = "США",
        ["CA"] = "Канада",
        ["MX"] = "Мексика",
        ["BR"] = "Бразилия",
        ["AR"] = "Аргентина",
        ["CL"] = "Чили",
        ["CN"] = "Китай",
        ["HK"] = "Гонконг",
        ["TW"] = "Тайвань",
        ["JP"] = "Япония",
        ["KR"] = "Южная Корея",
        ["IN"] = "Индия",
        ["SG"] = "Сингапур",
        ["TH"] = "Таиланд",
        ["VN"] = "Вьетнам",
        ["ID"] = "Индонезия",
        ["MY"] = "Малайзия",
        ["PH"] = "Филиппины",
        ["IL"] = "Израиль",
        ["AE"] = "ОАЭ",
        ["SA"] = "Саудовская Аравия",
        ["QA"] = "Катар",
        ["IR"] = "Иран",
        ["EG"] = "Египет",
        ["ZA"] = "ЮАР",
        ["AU"] = "Австралия",
        ["NZ"] = "Новая Зеландия",
    };
}

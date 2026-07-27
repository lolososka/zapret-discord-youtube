using System.Globalization;
using System.Net;
using System.Text;

namespace ZapretGui.Core;

public enum UserListKind
{
    BypassDomains,
    ExcludedDomains,
    ExcludedIps,
}

public sealed class UserListsSnapshot
{
    public List<string> BypassDomains { get; init; } = new();
    public List<string> ExcludedDomains { get; init; } = new();
    public List<string> ExcludedIps { get; init; } = new();
}

/// <summary>
/// Безопасный редактор только пользовательских списков zapret. Поставляемые и скачиваемые
/// базы сюда намеренно не входят: обновление может их заменить, а ipset-all ещё и хранит режим IPSet.
/// </summary>
public static class UserListManager
{
    private const string DomainStub = "domain.example.abc";
    private const string IpStub = "203.0.113.113/32";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static UserListsSnapshot Load() => new()
    {
        BypassDomains = LoadFile(UserListKind.BypassDomains),
        ExcludedDomains = LoadFile(UserListKind.ExcludedDomains),
        ExcludedIps = LoadFile(UserListKind.ExcludedIps),
    };

    public static string FileName(UserListKind kind) => kind switch
    {
        UserListKind.BypassDomains => "list-general-user.txt",
        UserListKind.ExcludedDomains => "list-exclude-user.txt",
        UserListKind.ExcludedIps => "ipset-exclude-user.txt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static IReadOnlyList<string> SplitInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        return text
            .Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    public static bool TryNormalize(UserListKind kind, string? input, out string normalized, out string error)
    {
        return kind == UserListKind.ExcludedIps
            ? TryNormalizeIp(input, out normalized, out error)
            : TryNormalizeDomain(input, out normalized, out error);
    }

    public static void Save(UserListsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var bypass = NormalizeList(UserListKind.BypassDomains, snapshot.BypassDomains);
        var excluded = NormalizeList(UserListKind.ExcludedDomains, snapshot.ExcludedDomains);
        var ips = NormalizeList(UserListKind.ExcludedIps, snapshot.ExcludedIps);

        var excludedSet = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        string? conflict = bypass.FirstOrDefault(excludedSet.Contains);
        if (conflict is not null)
            throw new InvalidDataException(
                $"Домен «{conflict}» одновременно добавлен в «Обходить» и «Не обходить».");

        Directory.CreateDirectory(AppPaths.Lists);
        WriteAtomic(UserListKind.BypassDomains, bypass);
        WriteAtomic(UserListKind.ExcludedDomains, excluded);
        WriteAtomic(UserListKind.ExcludedIps, ips);
    }

    private static List<string> LoadFile(UserListKind kind)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(AppPaths.Lists, FileName(kind));

        if (!File.Exists(path))
            return result;

        foreach (string raw in File.ReadLines(path))
        {
            string value = raw.Trim();
            if (value.Length == 0 || value.StartsWith('#') || value.StartsWith(';'))
                continue;
            if (IsStub(kind, value))
                continue;

            if (seen.Add(value))
                result.Add(value);
        }

        return result;
    }

    private static List<string> NormalizeList(UserListKind kind, IEnumerable<string> source)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in source)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (!TryNormalize(kind, raw, out string value, out string error))
                throw new InvalidDataException($"«{raw.Trim()}»: {error}");

            if (seen.Add(value))
                result.Add(value);
        }

        return result;
    }

    private static bool TryNormalizeDomain(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        string value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "введите домен или URL";
            return false;
        }

        if (value.IndexOfAny(new[] { '"', '\'', '<', '>', '\\' }) >= 0
            || value.Any(char.IsControl))
        {
            error = "недопустимые символы";
            return false;
        }

        string host = value;
        bool looksLikeUrl = value.Contains("://", StringComparison.Ordinal)
                            || value.Contains('/')
                            || value.Contains('?')
                            || value.Contains('#')
                            || value.LastIndexOf(':') > value.LastIndexOf(']');

        if (looksLikeUrl)
        {
            string candidate = value.Contains("://", StringComparison.Ordinal)
                ? value
                : "https://" + value;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "не удалось распознать URL";
                return false;
            }

            host = uri.Host;
        }

        host = host.Trim().TrimEnd('.');
        if (host.StartsWith("*.", StringComparison.Ordinal))
            host = host[2..];
        else if (host.StartsWith('.'))
            host = host[1..];

        if (host.Any(char.IsWhiteSpace))
        {
            error = "домен не должен содержать пробелы";
            return false;
        }

        if (IPAddress.TryParse(host, out _))
        {
            error = "для адресов используйте вкладку «Не трогать IP»";
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(host).ToLowerInvariant();
        }
        catch
        {
            error = "неверное международное имя домена";
            return false;
        }

        if (ascii.Length is < 3 or > 253 || !ascii.Contains('.'))
        {
            error = "ожидается полный домен, например example.com";
            return false;
        }

        foreach (string label in ascii.Split('.'))
        {
            if (label.Length is < 1 or > 63
                || label[0] == '-'
                || label[^1] == '-'
                || label.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                error = "неверный формат домена";
                return false;
            }
        }

        normalized = ascii;
        return true;
    }

    private static bool TryNormalizeIp(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        string value = (input ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "введите IP-адрес или подсеть";
            return false;
        }

        string addressPart = value;
        int? prefix = null;
        int slash = value.IndexOf('/');

        if (slash >= 0)
        {
            if (slash != value.LastIndexOf('/'))
            {
                error = "слишком много символов /";
                return false;
            }

            addressPart = value[..slash].Trim();
            if (!int.TryParse(value[(slash + 1)..].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int parsedPrefix))
            {
                error = "неверная длина префикса";
                return false;
            }

            prefix = parsedPrefix;
        }

        if (!IPAddress.TryParse(addressPart, out IPAddress? address))
        {
            error = "неверный IP-адрес";
            return false;
        }

        int maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefix is < 0 || prefix > maxPrefix)
        {
            error = $"префикс должен быть от 0 до {maxPrefix}";
            return false;
        }

        normalized = address.ToString();
        if (prefix is not null)
            normalized += "/" + prefix.Value.ToString(CultureInfo.InvariantCulture);

        return true;
    }

    private static void WriteAtomic(UserListKind kind, IReadOnlyList<string> entries)
    {
        string path = Path.Combine(AppPaths.Lists, FileName(kind));
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");

        string subject = kind switch
        {
            UserListKind.BypassDomains => "domains to bypass",
            UserListKind.ExcludedDomains => "domains excluded from bypass",
            UserListKind.ExcludedIps => "IP addresses and networks excluded from bypass",
            _ => "user entries",
        };

        var lines = new List<string>
        {
            $"# Managed by Zapret GUI: {subject}. One entry per line.",
        };

        if (entries.Count > 0)
            lines.AddRange(entries);
        else
            lines.Add(kind == UserListKind.ExcludedIps ? IpStub : DomainStub);

        try
        {
            File.WriteAllText(temp, string.Join("\r\n", lines) + "\r\n", Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Основной файл уже сохранён либо исходная ошибка важнее очистки временного файла.
            }
        }
    }

    private static bool IsStub(UserListKind kind, string value) =>
        string.Equals(
            value,
            kind == UserListKind.ExcludedIps ? IpStub : DomainStub,
            StringComparison.OrdinalIgnoreCase);
}

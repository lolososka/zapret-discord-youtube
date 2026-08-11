using System.IO;
using System.Text;

namespace ZapretGui.Core;

/// <summary>Режим игрового фильтра — подставляется вместо %GameFilter*% из .bat.</summary>
public enum GameFilterMode
{
    Disabled,
    All,
    Tcp,
    Udp
}

/// <summary>Одна стратегия обхода — распарсенный .bat-файл Flowseal.</summary>
public sealed class Strategy : ObservableObject
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Variant { get; init; } = string.Empty;
    public string RawCommandLine { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Одна фраза без терминов — то, что видно в строке списка.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Технический разбор аргументов — только для подсказки и диалога «Подробнее».</summary>
    public string TechnicalSummary { get; init; } = string.Empty;

    public int SortKey { get; init; }

    /// <summary>
    /// general и general (ALT). В интерфейсе это «обычный старт», а не «рекомендуется»:
    /// рабочая стратегия зависит от провайдера и заранее не угадывается.
    /// </summary>
    public bool IsRecommended { get; init; }

    private bool _isFavorite;
    /// <summary>Отмечена звёздочкой пользователем. Хранится в StrategyPreferences.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set => Set(ref _isFavorite, value);
    }

    private bool _hasWorked;
    /// <summary>Проработала без падения дольше минуты — «работала у вас».</summary>
    public bool HasWorked
    {
        get => _hasWorked;
        set => Set(ref _hasWorked, value);
    }

    private StrategyTestResult? _lastAutoPickResult;
    private GameFilterMode _lastAutoPickMode;

    /// <summary>Есть актуальный результат для неизменившейся команды стратегии.</summary>
    public bool HasAutoPickResult => _lastAutoPickResult is not null;

    public bool AutoPickPassedAny => _lastAutoPickResult is { OkCount: > 0 };

    public bool AutoPickPassedAll => _lastAutoPickResult is { TotalCount: > 0 } result
                                     && result.OkCount == result.TotalCount;

    public int AutoPickOkCount => _lastAutoPickResult?.OkCount ?? -1;

    public int AutoPickLatencySort => _lastAutoPickResult is { AverageLatencyMs: > 0 } result
        ? result.AverageLatencyMs
        : int.MaxValue;

    public string AutoPickResultText
    {
        get
        {
            if (_lastAutoPickResult is not { } result)
                return string.Empty;

            var latency = result.AverageLatencyMs > 0
                ? $" · {result.AverageLatencyMs} мс"
                : string.Empty;
            return $"тест {result.OkCount}/{result.TotalCount}{latency}";
        }
    }

    public string AutoPickResultTooltip
    {
        get
        {
            if (_lastAutoPickResult is not { } result)
                return string.Empty;

            var heading = $"Проверено {StrategyTestHistory.LocalTimeText(result.TestedAtUtc)}";
            var mode = StrategyTestHistory.ModeText(_lastAutoPickMode);
            return string.IsNullOrWhiteSpace(result.Detail)
                ? $"{heading}\n{mode}"
                : $"{result.Detail}\n{heading}\n{mode}";
        }
    }

    public void ApplyAutoPickResult(StrategyTestResult? result, GameFilterMode mode)
    {
        _lastAutoPickResult = result;
        _lastAutoPickMode = mode;
        RaiseMany(nameof(HasAutoPickResult), nameof(AutoPickPassedAny),
                  nameof(AutoPickPassedAll), nameof(AutoPickOkCount),
                  nameof(AutoPickLatencySort), nameof(AutoPickResultText),
                  nameof(AutoPickResultTooltip));
    }
}

public static class StrategyParser
{
    private const string WinWsToken = "winws.exe";

    // ---------- публичный API ----------

    public static List<Strategy> LoadAll()
    {
        var result = new List<Strategy>();
        try
        {
            if (!Directory.Exists(AppPaths.Root))
                return result;

            foreach (var path in Directory.EnumerateFiles(AppPaths.Root, "*.bat", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith("service", StringComparison.OrdinalIgnoreCase))
                    continue;

                var s = Load(path);
                if (s != null)
                    result.Add(s);
            }
        }
        catch
        {
            return result;
        }

        // Порядок должен совпадать с меню service.bat: там PowerShell сортирует имена файлов,
        // дополняя цифры нулями, из-за чего «(ALT)» идёт перед «(ALT2)», а «(FAKE TLS AUTO ALT)» —
        // перед «(FAKE TLS AUTO)» (пробел меньше закрывающей скобки). Ordinal() кодирует ровно это.
        result.Sort(static (a, b) =>
        {
            int oa = Ordinal(a.Variant);
            int ob = Ordinal(b.Variant);
            if (oa != ob)
                return oa.CompareTo(ob);

            return string.CompareOrdinal(NaturalKey(a.FileName), NaturalKey(b.FileName));
        });

        // Контракт требует плотную нумерацию 0,1,2… в порядке меню service.bat.
        for (int i = 0; i < result.Count; i++)
            result[i] = CloneWithSortKey(result[i], i);

        return result;
    }

    public static Strategy? Load(string batPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(batPath) || !File.Exists(batPath))
                return null;

            var lines = ReadLines(batPath);
            string? raw = ExtractCommandLine(lines);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var fileName = Path.GetFileName(batPath);
            var name = Path.GetFileNameWithoutExtension(batPath);
            var variant = PrettyVariant(ExtractVariant(name));
            var probe = BuildArguments(raw, GameFilterMode.All);   // для анализа удобнее развёрнутый вид

            return new Strategy
            {
                FilePath = Path.GetFullPath(batPath),
                FileName = fileName,
                Name = name,
                DisplayName = BuildDisplayName(name, variant),
                Variant = variant,
                RawCommandLine = raw,
                Tags = BuildTags(raw, probe),
                Summary = BuildSummary(probe),
                TechnicalSummary = BuildTechnicalSummary(probe),
                SortKey = Ordinal(variant),
                IsRecommended = name.Equals("general", StringComparison.OrdinalIgnoreCase)
                                || name.Equals("general (ALT)", StringComparison.OrdinalIgnoreCase),
                };
        }
        catch
        {
            return null;
        }
    }

    public static string BuildArguments(Strategy s, GameFilterMode mode)
        => BuildArguments(s?.RawCommandLine ?? string.Empty, mode);

    // ---------- разбор .bat ----------

    private static string[] ReadLines(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            // .bat из старых сборок бывает в однобайтовой кодировке — аргументы всё равно ASCII
            text = Encoding.Latin1.GetString(bytes);
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return text.Split('\n');
    }

    /// <summary>Берёт всё после закрывающей кавычки "%BIN%winws.exe" и склеивает строки по каретке.</summary>
    private static string? ExtractCommandLine(string[] lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.TrimStart().StartsWith("::", StringComparison.Ordinal))
                continue;
            if (l.IndexOf(WinWsToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                start = i;
                break;
            }
        }
        if (start < 0)
            return null;

        int idx = lines[start].IndexOf(WinWsToken, StringComparison.OrdinalIgnoreCase) + WinWsToken.Length;
        int quote = lines[start].IndexOf('"', idx);
        string first = quote >= 0 ? lines[start][(quote + 1)..] : lines[start][idx..];

        var sb = new StringBuilder();
        string current = first;
        int cursor = start;

        while (true)
        {
            string t = current.TrimEnd();
            bool cont = t.EndsWith('^');
            if (cont)
                t = t[..^1].TrimEnd();

            t = t.Trim();
            if (t.Length > 0)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(t);
            }

            if (!cont)
                break;

            cursor++;
            if (cursor >= lines.Length)
                break;
            current = lines[cursor];
        }

        // cmd экранирует восклицательный знак как ^! (см. EXCL_MARK в service.bat) — winws получает просто !
        return Collapse(sb.ToString()).Replace("^!", "!");
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (c == ' ' || c == '\t')
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string BuildArguments(string raw, GameFilterMode mode)
    {
        string tcp, udp, any;
        switch (mode)
        {
            case GameFilterMode.All:
                tcp = "1024-65535"; udp = "1024-65535"; any = "1024-65535"; break;
            case GameFilterMode.Tcp:
                tcp = "1024-65535"; udp = "12"; any = "1024-65535"; break;
            case GameFilterMode.Udp:
                tcp = "12"; udp = "1024-65535"; any = "1024-65535"; break;
            default:
                tcp = "12"; udp = "12"; any = "12"; break;
        }

        string s = raw;
        s = ReplaceCI(s, "%GameFilterTCP%", tcp);
        s = ReplaceCI(s, "%GameFilterUDP%", udp);
        s = ReplaceCI(s, "%GameFilter%", any);
        s = ReplaceCI(s, "%BIN%", AppPaths.Bin);
        s = ReplaceCI(s, "%LISTS%", AppPaths.Lists);
        s = ReplaceCI(s, "%~dp0", AppPaths.Root + "\\");
        return Collapse(s);
    }

    private static string ReplaceCI(string source, string what, string with)
    {
        if (string.IsNullOrEmpty(what))
            return source;

        int i = source.IndexOf(what, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return source;

        var sb = new StringBuilder(source.Length);
        int pos = 0;
        while (i >= 0)
        {
            sb.Append(source, pos, i - pos).Append(with);
            pos = i + what.Length;
            i = source.IndexOf(what, pos, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(source, pos, source.Length - pos);
        return sb.ToString();
    }

    // ---------- имя / вариант / порядок ----------

    private static string ExtractVariant(string name)
    {
        int open = name.IndexOf('(');
        int close = name.LastIndexOf(')');
        if (open < 0 || close <= open)
            return string.Empty;
        return name[(open + 1)..close].Trim();
    }

    /// <summary>"ALT11" -> "ALT 11", "FAKE TLS AUTO ALT2" -> "FAKE TLS AUTO ALT 2".</summary>
    private static string PrettyVariant(string variant)
    {
        if (variant.Length == 0)
            return string.Empty;

        int k = variant.Length;
        while (k > 0 && char.IsDigit(variant[k - 1]))
            k--;

        if (k == 0 || k == variant.Length)
            return variant;

        return variant[..k].TrimEnd() + " " + variant[k..];
    }

    private static string BuildDisplayName(string name, string variant)
    {
        int open = name.IndexOf('(');
        string baseName = (open > 0 ? name[..open] : name).Trim();
        if (baseName.Length > 0)
            baseName = char.ToUpperInvariant(baseName[0]) + baseName[1..].ToLowerInvariant();

        return variant.Length == 0 ? baseName : baseName + " " + variant;
    }

    private static int Ordinal(string prettyVariant)
    {
        if (prettyVariant.Length == 0)
            return 0;

        string v = prettyVariant.ToUpperInvariant();
        int num = 0;
        int k = v.Length;
        while (k > 0 && char.IsDigit(v[k - 1]))
            k--;

        if (k < v.Length && int.TryParse(v[k..], out int parsed))
            num = parsed;

        // Порядок семейств взят из меню service.bat (Sort-Object по имени с дополнением цифр нулями)
        string word = v[..k].Trim();
        int family = word switch
        {
            "ALT" => 1,
            "EXP" => 2,
            "FAKE TLS AUTO ALT" => 3,
            "FAKE TLS AUTO" => 4,
            "SIMPLE FAKE ALT" => 5,
            "SIMPLE FAKE" => 6,
            _ => 9
        };
        return family * 100 + num;
    }

    /// <summary>
    /// Ключ натуральной сортировки, повторяющий Sort-Object из service.bat: цифровые группы
    /// дополняются нулями до 8 знаков, а вес символов идёт как в культурном сравнении —
    /// пробел &lt; пунктуация &lt; цифры &lt; буквы.
    /// </summary>
    private static string NaturalKey(string name)
    {
        const char wSpace = (char)1;
        const char wPunct = (char)2;
        const char wDigit = (char)3;
        const char wLetter = (char)4;

        var sb = new StringBuilder(name.Length * 2 + 16);
        for (int i = 0; i < name.Length;)
        {
            char c = name[i];
            if (char.IsDigit(c))
            {
                int j = i;
                while (j < name.Length && char.IsDigit(name[j]))
                    j++;

                foreach (char d in name[i..j].PadLeft(8, '0'))
                {
                    sb.Append(wDigit);
                    sb.Append(d);
                }
                i = j;
            }
            else
            {
                if (c == ' ' || c == '\t')
                {
                    sb.Append(wSpace);
                }
                else if (char.IsLetter(c))
                {
                    sb.Append(wLetter);
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(wPunct);
                    sb.Append(c);
                }
                i++;
            }
        }
        return sb.ToString();
    }

    private static Strategy CloneWithSortKey(Strategy s, int key) => new()
    {
        FilePath = s.FilePath,
        FileName = s.FileName,
        Name = s.Name,
        DisplayName = s.DisplayName,
        Variant = s.Variant,
        RawCommandLine = s.RawCommandLine,
        Tags = s.Tags,
        Summary = s.Summary,
        TechnicalSummary = s.TechnicalSummary,
        SortKey = key,
        IsRecommended = s.IsRecommended
    };

    // ---------- анализ аргументов ----------

    private static bool Has(string s, string needle) => s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string? ArgValue(string s, string key)
    {
        int i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return null;

        int start = i + key.Length;
        int end = start;
        while (end < s.Length && !char.IsWhiteSpace(s[end]))
            end++;

        return end > start ? s[start..end] : null;
    }

    /// <summary>
    /// В строке списка помещается 4 чипа, и полезны там только понятные слова —
    /// что именно эта стратегия прикрывает. Технические метки живут в TechnicalSummary.
    /// </summary>
    private static List<string> BuildTags(string raw, string probe)
    {
        var tags = new List<string>(4);

        void Add(string t)
        {
            if (tags.Count < 4 && !tags.Contains(t))
                tags.Add(t);
        }

        if (Has(probe, "--filter-l7=discord") || Has(probe, "fake-discord") || Has(probe, "discord.media"))
            Add("Discord");
        if (Has(probe, "list-general.txt"))
            Add("YouTube");
        if (Has(probe, "list-google.txt"))
            Add("Google");
        if (Has(raw, "%GameFilterTCP%") || Has(raw, "%GameFilterUDP%") || Has(raw, "%GameFilter%"))
            Add("Игры");
        if (Has(probe, "fake-quic") || Has(probe, "filter-l7=quic"))
            Add("QUIC");

        return tags;
    }

    /// <summary>Технические метки — их место в подсказке и в диалоге «Подробнее».</summary>
    private static List<string> BuildTechnicalMarks(string probe)
    {
        var marks = new List<string>();

        void Add(string t)
        {
            if (!marks.Contains(t))
                marks.Add(t);
        }

        if (Has(probe, "hostfakesplit")) Add("hostfakesplit");
        if (Has(probe, "multidisorder")) Add("multidisorder");
        if (Has(probe, "fakedsplit")) Add("fakedsplit");
        if (Has(probe, "multisplit")) Add("multisplit");
        if (Has(probe, "syndata")) Add("syndata");
        if (Has(probe, "split-seqovl")) Add("seqovl");

        if (Has(probe, "fake-tls-mod")) Add("fake tls auto");
        else if (Has(probe, "dpi-desync-fake-tls")) Add("fake tls");

        string? fooling = ArgValue(probe, "--dpi-desync-fooling=");
        if (!string.IsNullOrEmpty(fooling)) Add("fooling " + fooling);

        if (Has(probe, "ipset-all.txt")) Add("ipset-all");

        return marks;
    }

    private static string DesyncPhrase(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "" => "стандартный обход DPI",
        "fake" => "подмена первого пакета (fake)",
        "fake,fakedsplit" => "фейк с последующим разрезанием пакета (fakedsplit)",
        "fakedsplit" => "разрезание пакета с фейком (fakedsplit)",
        "multisplit" => "разрезание запроса на части (multisplit)",
        "fake,multisplit" => "фейк вместе с разрезанием запроса (multisplit)",
        "multidisorder" => "разрезание с перестановкой сегментов (multidisorder)",
        "syndata,multidisorder" => "подмена данных в SYN и перестановка сегментов (multidisorder)",
        "fake,multidisorder" => "фейк с перестановкой сегментов (multidisorder)",
        "hostfakesplit" => "разрезание по заголовку Host с подставным доменом (hostfakesplit)",
        "fake,hostfakesplit" => "фейк и разрезание по заголовку Host с подставным доменом (hostfakesplit)",
        "disorder" => "перестановка сегментов (disorder)",
        "split" => "разрезание запроса (split)",
        _ => "метод обхода " + (value ?? string.Empty)
    };

    /// <summary>
    /// Секция, отвечающая за обычный HTTPS-трафик — она определяет «характер» стратегии.
    /// Правила от точного к общему: у ALT5, например, вообще нет секции 80,443 со списком хостов,
    /// и без запасного правила описание съезжало на первую попавшуюся секцию (UDP/QUIC).
    /// </summary>
    private static string MainSegment(string probe)
    {
        var segments = probe.Split(" --new ", StringSplitOptions.RemoveEmptyEntries);

        return segments.FirstOrDefault(s => Has(s, "--filter-tcp=80,443") && Has(s, "list-general.txt"))
               ?? segments.FirstOrDefault(s => Has(s, "--filter-tcp=80,443"))
               ?? segments.FirstOrDefault(s => Has(s, "--filter-tcp=") && Has(s, "443") && Has(s, "--dpi-desync="))
               ?? probe;
    }

    /// <summary>Человеческая фраза без жаргона: что стратегия делает с запросом.</summary>
    private static string HumanDesync(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "" => "Почти не трогает соединение — самый мягкий вариант, подходит редко.",
        "fake" => "Подставляет фильтру ложный первый пакет, чтобы он не увидел адрес сайта.",
        "fakedsplit" or "fake,fakedsplit" => "Подставляет ложный пакет и разрезает запрос надвое.",
        "multisplit" => "Разбивает запрос на несколько частей, чтобы фильтр не собрал его целиком.",
        "fake,multisplit" => "Разбивает запрос на несколько частей и подделывает первый пакет — самый частый рабочий вариант.",
        "multidisorder" => "Разбивает запрос на части и отправляет их вперемешку.",
        "fake,multidisorder" => "Разбивает запрос на части, шлёт их вперемешку и добавляет ложный пакет.",
        "syndata,multidisorder" => "Начинает соединение с ложных данных, а сам запрос шлёт частями вперемешку.",
        "hostfakesplit" or "fake,hostfakesplit" => "Разрезает запрос по имени сайта и подставляет вместо него чужое.",
        "disorder" => "Отправляет части запроса в обратном порядке.",
        "split" => "Разрезает запрос надвое.",
        _ => "Нестандартный способ обхода — имеет смысл проверить, если остальные не помогли.",
    };

    private static string BuildSummary(string probe)
    {
        string main = MainSegment(probe);

        var sb = new StringBuilder(HumanDesync(ArgValue(main, "--dpi-desync=")));

        // Ровно одно уточнение: две фразы в строке 88 px читаются, три — уже нет.
        if (Has(main, "split-seqovl"))
            sb.Append(" Части идут внахлёст, чтобы фильтр не склеил их обратно.");
        else if (Has(probe, "fake-discord") || Has(probe, "--filter-l7=discord"))
            sb.Append(" Голосовые каналы Discord прикрывает отдельно.");
        else if (Has(probe, "fake-quic"))
            sb.Append(" Заодно глушит ускоренный протокол видео, чтобы YouTube шёл обычным путём.");
        else if (Has(main, "fake-tls-mod"))
            sb.Append(" Ложный пакет собирается на лету под каждый сайт.");

        return sb.ToString();
    }

    private static string BuildTechnicalSummary(string probe)
    {
        string main = MainSegment(probe);

        var sb = new StringBuilder();
        sb.Append("Для HTTPS применяется ").Append(DesyncPhrase(ArgValue(main, "--dpi-desync=")));

        string? fooling = ArgValue(main, "--dpi-desync-fooling=");
        if (!string.IsNullOrEmpty(fooling))
            sb.Append(" с обманом DPI (").Append(fooling).Append(')');

        if (Has(main, "split-seqovl"))
            sb.Append(" и перекрытие сегментов (seqovl)");

        if (Has(probe, "fake-quic"))
            sb.Append(", QUIC глушится фейковым initial-пакетом");

        if (Has(probe, "fake-discord") || Has(probe, "--filter-l7=discord"))
            sb.Append(", голос Discord и STUN получают отдельный fake-пакет");

        if (Has(main, "fake-tls-mod"))
            sb.Append(", TLS-фейк генерируется автоматически");

        sb.Append('.');

        var marks = BuildTechnicalMarks(probe);
        if (marks.Count > 0)
            sb.Append(" Ключевые параметры: ").Append(string.Join(", ", marks)).Append('.');

        return sb.ToString();
    }
}

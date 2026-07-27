namespace ZapretGui.Core;

/// <summary>
/// Пути к папке zapret и к пользовательским данным приложения.
/// Ни на что в проекте не ссылается — это самый нижний слой.
/// </summary>
public static class AppPaths
{
    private const string UserListsMarkerIp = "203.0.113.113/32";
    private const string UserListDomainStub = "domain.example.abc";

    static AppPaths()
    {
        string root;
        try
        {
            root = ResolveRoot();
        }
        catch
        {
            root = TrimSlash(AppContext.BaseDirectory);
        }

        Root = root;
        Bin = Root + "\\bin\\";
        Lists = Root + "\\lists\\";
        Utils = Root + "\\utils\\";
        WinWs = Bin + "winws.exe";
    }

    /// <summary>Папка, содержащая bin\winws.exe (без завершающего слэша).</summary>
    public static string Root { get; }

    /// <summary>Root + "\bin\" (со слэшем на конце).</summary>
    public static string Bin { get; }

    /// <summary>Root + "\lists\" (со слэшем на конце).</summary>
    public static string Lists { get; }

    /// <summary>Root + "\utils\" (со слэшем на конце).</summary>
    public static string Utils { get; }

    /// <summary>Полный путь к bin\winws.exe.</summary>
    public static string WinWs { get; }

    /// <summary>Найден ли настоящий корень zapret.</summary>
    public static bool IsValidRoot
    {
        get
        {
            try { return File.Exists(WinWs); }
            catch { return false; }
        }
    }

    /// <summary>%APPDATA%\ZapretGUI — создаётся при обращении.</summary>
    public static string DataDir
    {
        get
        {
            string dir;
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrWhiteSpace(appData))
                    appData = TrimSlash(AppContext.BaseDirectory);
                dir = Path.Combine(appData, "ZapretGUI");
            }
            catch
            {
                dir = Path.Combine(TrimSlash(AppContext.BaseDirectory), "ZapretGUI");
            }

            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch
            {
                // каталог может быть недоступен — путь всё равно возвращаем
            }

            return dir;
        }
    }

    /// <summary>DataDir\settings.json</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>DataDir\zapret-gui.log</summary>
    public static string LogFile => Path.Combine(DataDir, "zapret-gui.log");

    /// <summary>
    /// Создаёт пользовательские списки, если их нет. Пустой list-general-user.txt
    /// ломает winws.exe, поэтому в файлы всегда пишется заглушка.
    /// </summary>
    public static void EnsureUserLists()
    {
        try
        {
            if (!Directory.Exists(Lists))
                Directory.CreateDirectory(Lists);
        }
        catch
        {
            return;
        }

        WriteIfMissing(Path.Combine(Lists, "ipset-exclude-user.txt"),
            UserListsMarkerIp);

        WriteIfMissing(Path.Combine(Lists, "list-general-user.txt"),
            "# Never leave this file empty" + Environment.NewLine + UserListDomainStub);

        WriteIfMissing(Path.Combine(Lists, "list-exclude-user.txt"),
            UserListDomainStub);
    }

    private static void WriteIfMissing(string path, string content)
    {
        try
        {
            if (File.Exists(path))
                return;
            File.WriteAllText(path, content + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // права/диск — молча игнорируем
        }
    }

    private static string ResolveRoot()
    {
        var baseDir = TrimSlash(AppContext.BaseDirectory);
        var current = baseDir;

        // сам каталог и до 4 родительских: bin\winws.exe либо zapret-discord-youtube*\bin\winws.exe
        for (var i = 0; i <= 4; i++)
        {
            if (string.IsNullOrEmpty(current))
                break;

            if (HasWinWs(current))
                return TrimSlash(current);

            var nested = FindNestedZapret(current);
            if (nested != null)
                return TrimSlash(nested);

            var parent = SafeParent(current);
            if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }

        return baseDir;
    }

    private static bool HasWinWs(string dir)
    {
        try { return File.Exists(Path.Combine(dir, "bin", "winws.exe")); }
        catch { return false; }
    }

    private static string? FindNestedZapret(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return null;
            foreach (var sub in Directory.EnumerateDirectories(dir, "zapret-discord-youtube*"))
            {
                if (HasWinWs(sub))
                    return sub;
            }
        }
        catch
        {
            // отказ в доступе к каталогу
        }
        return null;
    }

    private static string? SafeParent(string dir)
    {
        try { return Directory.GetParent(dir)?.FullName; }
        catch { return null; }
    }

    private static string TrimSlash(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        var trimmed = path.TrimEnd('\\', '/');
        // корень диска "C:" -> оставляем "C:\"
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }
}

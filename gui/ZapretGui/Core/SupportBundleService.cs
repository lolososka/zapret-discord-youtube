using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretGui.Core;

/// <summary>Краткая статистика собранного обезличенного отчёта.</summary>
public readonly record struct SupportBundleResult(
    int DiagnosticCount,
    int ProbeCount,
    int LogLineCount,
    int FileCount);

/// <summary>
/// Собирает ZIP для обращения за помощью.
///
/// В отчёт намеренно не попадают настройки как файлы, содержимое пользовательских
/// списков, данные карточки внешнего IP, путь к установке и секрет Telegram.
/// Журнал и текст диагностики проходят дополнительное редактирование.
/// </summary>
public static class SupportBundleService
{
    private const int MaxLogLines = 2_000;
    private const int MaxFilesPerArea = 256;
    private const int MaxManifestBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex StandardStrategyFile = new(
        @"^general(?: \((?:ALT\d*|EXP\d*|FAKE TLS AUTO(?: ALT\d*)?|SIMPLE FAKE(?: ALT\d*)?)\))?\.bat$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SafeListFileNames = new(
        new[]
        {
            "ipset-all.txt",
            "ipset-all.txt.backup",
            "ipset-exclude.txt",
            "ipset-exclude-user.txt",
            "list-exclude.txt",
            "list-exclude-user.txt",
            "list-general.txt",
            "list-general-user.txt",
            "list-google.txt",
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SafeBinFileNames = new(
        new[]
        {
            "ACTIVE_DISCORD_UDP.bin",
            "ACTIVE_GAME_UDP.bin",
            "cygwin1.dll",
            "quic_initial_4pda.to.bin",
            "quic_initial_dbankcloud_ru.bin",
            "quic_initial_steamcommunity_com.bin",
            "quic_initial_tencent_com.bin",
            "quic_initial_www_google_com.bin",
            "stun.bin",
            "stun2.bin",
            "tls_clienthello_4pda_to.bin",
            "tls_clienthello_max_ru.bin",
            "tls_clienthello_www_google_com.bin",
            "WinDivert.dll",
            "WinDivert64.sys",
            "winws.exe",
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SafeUtilsFileNames = new(
        new[] { "check_updates.enabled" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Снимок коллекций снимается до ухода с UI-потока, а ZIP создаётся в фоне.
    /// Публикация выполняется заменой временного файла в той же папке.
    /// </summary>
    public static Task<SupportBundleResult> CreateAsync(
        string destinationPath,
        AppState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(state);

        var snapshot = Capture(state);
        return Task.Run(
            () => WriteAtomic(destinationPath, snapshot, cancellationToken),
            cancellationToken);
    }

    private static Snapshot Capture(AppState state)
    {
        var telegram = TelegramProxy.Instance;
        var settings = AppSettings.Current;

        var diagnostics = state.Diagnostics
            .Select(static result => new DiagnosticSnapshot(
                result.Id,
                result.Title,
                result.Status.ToString(),
                result.Detail,
                !string.IsNullOrWhiteSpace(result.FixLabel)))
            .ToArray();

        var probes = state.Probes
            .Select(static result => new ProbeSnapshot(
                result.Site.Name,
                result.Ok,
                result.LatencyMs,
                result.Error))
            .ToArray();

        var log = state.Log
            .TakeLast(MaxLogLines)
            .Select(static line => new LogSnapshot(
                line.Time,
                line.Level.ToString(),
                line.Text))
            .ToArray();

        var privateStrategyNames = state.Strategies
            .Where(static strategy =>
                !IsStandardStrategyName(strategy.FileName))
            .SelectMany(static strategy => new[]
            {
                strategy.FileName,
                strategy.Name,
                strategy.DisplayName,
                strategy.Variant,
            })
            .Cast<string?>()
            .ToList();
        if (!IsStandardStrategyName(state.InstalledServiceStrategy))
            privateStrategyNames.Add(state.InstalledServiceStrategy);

        return new Snapshot(
            CreatedUtc: DateTimeOffset.UtcNow,
            GuiVersion: UpdateService.LocalVersion,
            AssemblyVersion: typeof(SupportBundleService).Assembly.GetName().Version?.ToString() ?? "unknown",
            UpdateAvailableVersion: state.UpdateAvailableVersion,
            BypassState: state.BypassState.ToString(),
            ServiceState: state.ServiceState.ToString(),
            SelectedStrategy: StrategyLabel(state.SelectedStrategy?.FileName),
            InstalledServiceStrategy: StrategyLabel(state.InstalledServiceStrategy),
            GameFilter: state.GameFilter.ToString(),
            IpsetMode: state.IpsetMode.ToString(),
            IsApplyingStrategy: state.IsApplyingStrategy,
            IsDiagnosticsRunning: state.IsDiagnosticsRunning,
            StrategyCount: state.Strategies.Count,
            RootValid: AppPaths.IsValidRoot,
            TelegramState: telegram.State.ToString(),
            TelegramExecutableFound: telegram.IsFound,
            TelegramAutoStart: telegram.AutoStartWithBypass,
            IsAdministrator: Elevation.IsAdministrator,
            Settings: new SettingsSnapshot(
                settings.AutoStartBypass,
                settings.StartWithWindows,
                settings.StartMinimized,
                settings.MinimizeToTray,
                settings.CloseToTray,
                settings.CheckUpdatesOnLaunch,
                settings.ReducedMotion,
                settings.AutoRestartOnCrash),
            Diagnostics: diagnostics,
            Probes: probes,
            Log: log,
            SensitiveValues: SensitiveValues(
                telegram.Secret,
                privateStrategyNames));
    }

    private static SupportBundleResult WriteAtomic(
        string destinationPath,
        Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Не удалось определить папку отчёта.");

        Directory.CreateDirectory(directory);

        var temp = Path.Combine(
            directory,
            "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            var redactor = new ReportRedactor(snapshot.SensitiveValues);
            var package = ReadPackageVersions(cancellationToken);
            var files = CollectFileInventory(
                redactor,
                package?.ManagedFiles ?? Array.Empty<string>(),
                cancellationToken);

            using (var output = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteTextEntry(archive, "README.txt", BuildReadme(snapshot));
                    WriteTextEntry(archive, "system.txt", BuildSystem(snapshot, package, redactor));
                    WriteTextEntry(archive, "diagnostics.txt", BuildDiagnostics(snapshot, redactor));
                    WriteTextEntry(archive, "connectivity.txt", BuildProbes(snapshot, redactor));
                    WriteTextEntry(archive, "journal.txt", BuildJournal(snapshot, redactor));
                    WriteTextEntry(archive, "files.txt", BuildFiles(files, redactor));
                }

                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PublishAtomic(temp, destination);

            return new SupportBundleResult(
                snapshot.Diagnostics.Length,
                snapshot.Probes.Length,
                snapshot.Log.Length,
                files.Count);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void PublishAtomic(string temp, string destination)
    {
        if (File.Exists(destination))
        {
            File.Replace(temp, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        try
        {
            File.Move(temp, destination);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Файл мог появиться между проверкой и Move. Replace всё ещё выполняется
            // в одной папке и не оставляет частично записанный ZIP.
            File.Replace(temp, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8, bufferSize: 4096, leaveOpen: false);
        writer.Write(text);
    }

    private static string BuildReadme(Snapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Zapret Control Center — обезличенный отчёт для поддержки")
          .Append("Создан (UTC): ")
          .AppendLine(snapshot.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))
          .AppendLine()
          .AppendLine("Что включено:")
          .AppendLine("- версии и состояния компонентов;")
          .AppendLine("- результаты диагностики и проверки доступности;")
          .AppendLine("- отредактированный журнал;")
          .AppendLine("- только имена и размеры ключевых файлов.")
          .AppendLine()
          .AppendLine("Что не включено:")
          .AppendLine("- содержимое любых файлов из папки lists;")
          .AppendLine("- внешний IP, город и провайдер;")
          .AppendLine("- Telegram secret и ссылка настройки прокси;")
          .AppendLine("- имя пользователя, имя компьютера и полные локальные пути;")
          .AppendLine("- settings.json, telegram.json и strategies.json.")
          .AppendLine()
          .AppendLine("Перед отправкой отчёт можно открыть обычным архиватором и проверить вручную.");
        return sb.ToString();
    }

    private static string BuildSystem(
        Snapshot snapshot,
        PackageVersions? package,
        ReportRedactor redactor)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Версии]");
        AppendTrusted(sb, "GUI", snapshot.GuiVersion);
        AppendTrusted(sb, "Сборка", snapshot.AssemblyVersion);
        AppendRedacted(sb, "Flowseal", package?.UpstreamVersion ?? "не определена", redactor);
        AppendRedacted(sb, "Манифест GUI", package?.GuiVersion ?? "не определена", redactor);
        AppendRedacted(sb, "Тег релиза", package?.Tag ?? "не определён", redactor);
        AppendRedacted(sb, "Коммит Flowseal", package?.UpstreamCommit ?? "не определён", redactor);
        AppendRedacted(sb, "Коммит форка", package?.ForkCommit ?? "не определён", redactor);
        AppendRedacted(sb, "Доступное обновление", snapshot.UpdateAvailableVersion ?? "нет", redactor);
        AppendTrusted(sb, ".NET", RuntimeInformation.FrameworkDescription);
        AppendTrusted(sb, "ОС", RuntimeInformation.OSDescription);
        AppendTrusted(sb, "Архитектура ОС", RuntimeInformation.OSArchitecture.ToString());
        AppendTrusted(sb, "Архитектура процесса", RuntimeInformation.ProcessArchitecture.ToString());

        sb.AppendLine().AppendLine("[Состояние]");
        AppendTrusted(sb, "Корень zapret найден", YesNo(snapshot.RootValid));
        AppendTrusted(sb, "Права администратора", YesNo(snapshot.IsAdministrator));
        AppendTrusted(sb, "Обход", snapshot.BypassState);
        AppendTrusted(sb, "Служба", snapshot.ServiceState);
        AppendRedacted(sb, "Выбранная стратегия", snapshot.SelectedStrategy ?? "не выбрана", redactor);
        AppendRedacted(sb, "Стратегия службы", snapshot.InstalledServiceStrategy ?? "не определена", redactor);
        AppendTrusted(sb, "Игровой фильтр", snapshot.GameFilter);
        AppendTrusted(sb, "IPSet", snapshot.IpsetMode);
        AppendTrusted(sb, "Применение стратегии", YesNo(snapshot.IsApplyingStrategy));
        AppendTrusted(sb, "Диагностика выполняется", YesNo(snapshot.IsDiagnosticsRunning));
        AppendTrusted(sb, "Найдено стратегий", snapshot.StrategyCount.ToString(CultureInfo.InvariantCulture));

        sb.AppendLine().AppendLine("[Telegram]");
        AppendTrusted(sb, "TgWsProxy", snapshot.TelegramState);
        AppendTrusted(sb, "Исполняемый файл найден", YesNo(snapshot.TelegramExecutableFound));
        AppendTrusted(sb, "Автозапуск с обходом", YesNo(snapshot.TelegramAutoStart));
        AppendTrusted(sb, "Secret", "не включён");

        sb.AppendLine().AppendLine("[Настройки]");
        AppendTrusted(sb, "Автозапуск обхода", YesNo(snapshot.Settings.AutoStartBypass));
        AppendTrusted(sb, "Запуск с Windows", YesNo(snapshot.Settings.StartWithWindows));
        AppendTrusted(sb, "Запускать свёрнутым", YesNo(snapshot.Settings.StartMinimized));
        AppendTrusted(sb, "Сворачивать в трей", YesNo(snapshot.Settings.MinimizeToTray));
        AppendTrusted(sb, "Закрывать в трей", YesNo(snapshot.Settings.CloseToTray));
        AppendTrusted(sb, "Проверять обновления", YesNo(snapshot.Settings.CheckUpdatesOnLaunch));
        AppendTrusted(sb, "Спокойный режим", YesNo(snapshot.Settings.ReducedMotion));
        AppendTrusted(sb, "Автовосстановление после сбоя", YesNo(snapshot.Settings.AutoRestartOnCrash));

        return sb.ToString();
    }

    private static string BuildDiagnostics(Snapshot snapshot, ReportRedactor redactor)
    {
        var sb = new StringBuilder();
        sb.Append("Проверок в снимке: ")
          .AppendLine(snapshot.Diagnostics.Length.ToString(CultureInfo.InvariantCulture))
          .AppendLine();

        if (snapshot.Diagnostics.Length == 0)
        {
            sb.AppendLine("Диагностика ещё не запускалась.");
            return sb.ToString();
        }

        foreach (var item in snapshot.Diagnostics)
        {
            sb.Append('[')
              .Append(redactor.Clean(item.Status))
              .Append("] ")
              .AppendLine(redactor.Clean(item.Title));
            sb.Append("ID: ").AppendLine(redactor.Clean(item.Id));
            sb.Append("Результат: ").AppendLine(redactor.Clean(item.Detail));
            sb.Append("Автоисправление: ").AppendLine(item.HasFix ? "доступно" : "нет");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildProbes(Snapshot snapshot, ReportRedactor redactor)
    {
        var sb = new StringBuilder();
        sb.Append("Проверок доступности в снимке: ")
          .AppendLine(snapshot.Probes.Length.ToString(CultureInfo.InvariantCulture))
          .AppendLine();

        if (snapshot.Probes.Length == 0)
        {
            sb.AppendLine("Проверка доступности ещё не запускалась.");
            return sb.ToString();
        }

        foreach (var item in snapshot.Probes)
        {
            sb.Append(item.Ok ? "[OK] " : "[FAIL] ")
              .Append(redactor.Clean(item.Name))
              .Append(" · ")
              .Append(item.LatencyMs.ToString(CultureInfo.InvariantCulture))
              .AppendLine(" мс");

            if (!string.IsNullOrWhiteSpace(item.Error))
                sb.Append("Причина: ").AppendLine(redactor.Clean(item.Error));
        }

        return sb.ToString();
    }

    private static string BuildJournal(Snapshot snapshot, ReportRedactor redactor)
    {
        var sb = new StringBuilder();
        sb.Append("Строк в снимке: ")
          .AppendLine(snapshot.Log.Length.ToString(CultureInfo.InvariantCulture))
          .AppendLine("Время приведено к UTC. Адреса, пути и секреты заменены маркерами.")
          .AppendLine();

        if (snapshot.Log.Length == 0)
        {
            sb.AppendLine("Журнал пуст.");
            return sb.ToString();
        }

        foreach (var line in snapshot.Log)
        {
            sb.Append(line.Time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append("  ")
              .Append(line.Level.ToUpperInvariant().PadRight(7))
              .Append("  ")
              .AppendLine(redactor.Clean(line.Text));
        }

        return sb.ToString();
    }

    private static string BuildFiles(
        IReadOnlyList<FileSnapshot> files,
        ReportRedactor redactor)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Содержимое файлов не читается и не включается.")
          .AppendLine("Для пользовательских списков указаны только имя и размер.")
          .AppendLine();

        foreach (var file in files)
        {
            sb.Append(file.RelativeName)
              .Append(" | ")
              .Append(file.Exists
                  ? file.Size is long size
                      ? size.ToString("N0", CultureInfo.InvariantCulture) + " bytes"
                      : "размер недоступен"
                  : "не найден");

            if (!string.IsNullOrWhiteSpace(file.Version))
                sb.Append(" | version ").Append(redactor.Clean(file.Version));

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<FileSnapshot> CollectFileInventory(
        ReportRedactor redactor,
        IReadOnlyCollection<string> managedFiles,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        var managed = new HashSet<string>(
            managedFiles.Select(NormalizeManifestPath),
            StringComparer.OrdinalIgnoreCase);

        AddFile(files, "root", Path.Combine(AppPaths.Root, "ZapretGUI.exe"), redactor);
        AddFile(files, "root", Path.Combine(AppPaths.Root, "service.bat"), redactor);
        AddFile(files, "bin", AppPaths.WinWs, redactor);
        AddFile(files, "bin", Path.Combine(AppPaths.Bin, "WinDivert.dll"), redactor);
        AddFile(files, "bin", Path.Combine(AppPaths.Bin, "WinDivert64.sys"), redactor);

        AddRootBatches(
            files,
            AppPaths.Root,
            managed,
            redactor,
            cancellationToken);

        AddArea(
            files,
            "bin",
            AppPaths.Bin,
            managed,
            static extension =>
                extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sys", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase),
            redactor,
            cancellationToken);

        AddLists(
            files,
            AppPaths.Lists,
            managed,
            redactor,
            cancellationToken);

        AddArea(
            files,
            "utils",
            AppPaths.Utils,
            managed,
            static extension =>
                extension.Equals(".enabled", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase),
            redactor,
            cancellationToken);

        return files.Values
            .OrderBy(static file => file.RelativeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddRootBatches(
        IDictionary<string, FileSnapshot> files,
        string directory,
        ISet<string> managedFiles,
        ReportRedactor redactor,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            var customIndex = 0;
            foreach (var path in Directory
                         .EnumerateFiles(directory, "*.bat", SearchOption.TopDirectoryOnly)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxFilesPerArea))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(path))
                    continue;

                var fileName = Path.GetFileName(path);
                var relative = NormalizeManifestPath(fileName);
                var hasSafeName =
                    fileName.Equals("service.bat", StringComparison.OrdinalIgnoreCase)
                    || (managedFiles.Contains(relative)
                        && IsStandardStrategyName(fileName));
                var reportName = hasSafeName
                    ? "root/" + redactor.CleanFileName(fileName)
                    : "root/custom-strategy-"
                      + (++customIndex).ToString("00", CultureInfo.InvariantCulture)
                      + ".bat";

                AddFile(files, reportName, path);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Имена и исключение не выводим: оба могут содержать личные данные.
        }
    }

    private static void AddLists(
        IDictionary<string, FileSnapshot> files,
        string directory,
        ISet<string> managedFiles,
        ReportRedactor redactor,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            var customIndex = 0;
            foreach (var path in Directory
                         .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxFilesPerArea))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(path);
                if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".backup", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsReparsePoint(path))
                    continue;

                var fileName = Path.GetFileName(path);
                var relative = "lists/" + NormalizeManifestPath(fileName);
                var reportName = SafeListFileNames.Contains(fileName)
                                 && (managedFiles.Contains(relative)
                                     || fileName.Contains(
                                         "-user.",
                                         StringComparison.OrdinalIgnoreCase))
                    ? "lists/" + redactor.CleanFileName(fileName)
                    : "lists/custom-list-"
                      + (++customIndex).ToString("00", CultureInfo.InvariantCulture)
                      + extension.ToLowerInvariant();

                AddFile(files, reportName, path);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Содержимое и исходные имена пользовательских файлов не выводятся.
        }
    }

    private static void AddArea(
        IDictionary<string, FileSnapshot> files,
        string area,
        string directory,
        ISet<string> managedFiles,
        Func<string, bool> includeExtension,
        ReportRedactor redactor,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            var count = 0;
            var customIndex = 0;
            foreach (var path in Directory
                         .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count >= MaxFilesPerArea)
                    break;

                if (!includeExtension(Path.GetExtension(path)))
                    continue;

                if (IsReparsePoint(path))
                    continue;

                var fileName = Path.GetFileName(path);
                var relative = area + "/" + NormalizeManifestPath(fileName);
                var isManaged = managedFiles.Contains(relative)
                                && IsSafeManagedAreaFile(area, fileName);
                var reportName = isManaged
                    ? area + "/" + redactor.CleanFileName(fileName)
                    : area + "/custom-file-"
                      + (++customIndex).ToString("00", CultureInfo.InvariantCulture)
                      + Path.GetExtension(fileName).ToLowerInvariant();

                AddFile(files, reportName, path, includeVersion: isManaged);
                count++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // В отчёте не показываем исключение: оно само может содержать полный путь.
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void AddFile(
        IDictionary<string, FileSnapshot> files,
        string area,
        string path,
        ReportRedactor redactor)
    {
        var rawName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(rawName))
            return;

        var relativeName = area + "/" + redactor.CleanFileName(rawName);
        AddFile(files, relativeName, path);
    }

    private static void AddFile(
        IDictionary<string, FileSnapshot> files,
        string relativeName,
        string path,
        bool includeVersion = true)
    {
        if (files.ContainsKey(relativeName))
            return;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                files[relativeName] = new FileSnapshot(relativeName, Exists: false, Size: null, Version: null);
                return;
            }

            long? size;
            try { size = info.Length; }
            catch { size = null; }

            files[relativeName] = new FileSnapshot(
                relativeName,
                Exists: true,
                Size: size,
                Version: includeVersion ? SafeFileVersion(path) : null);
        }
        catch
        {
            files[relativeName] = new FileSnapshot(relativeName, Exists: true, Size: null, Version: null);
        }
    }

    private static string? SafeFileVersion(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".sys", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return SafeVersion(FileVersionInfo.GetVersionInfo(path).FileVersion);
        }
        catch
        {
            return null;
        }
    }

    private static PackageVersions? ReadPackageVersions(CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppPaths.Root, PortableUpdateInstaller.ManifestFileName);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaxManifestBytes)
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);

            var gui = SafeVersion(ReadString(document.RootElement, "guiVersion"));
            var upstream = SafeVersion(ReadString(document.RootElement, "upstreamVersion"));
            var tag = SafeVersion(ReadString(document.RootElement, "tag"));
            var upstreamCommit = SafeVersion(ReadString(document.RootElement, "upstreamCommit"));
            var forkCommit = SafeVersion(ReadString(document.RootElement, "forkCommit"));
            var managedFiles = ReadManagedFiles(document.RootElement);

            return gui is null
                   && upstream is null
                   && tag is null
                   && upstreamCommit is null
                   && forkCommit is null
                ? null
                : new PackageVersions(
                    gui ?? "не определена",
                    upstream ?? "не определена",
                    tag ?? "не определён",
                    upstreamCommit ?? "не определён",
                    forkCommit ?? "не определён",
                    managedFiles);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.String)
                continue;

            return property.Value.GetString();
        }

        return null;
    }

    private static string[] ReadManagedFiles(JsonElement root)
    {
        if (!TryGetProperty(root, "files", out var files) || files.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.EnumerateArray())
        {
            var raw = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var normalized = NormalizeManifestPath(raw);
            if (normalized.Length == 0
                || normalized.StartsWith("../", StringComparison.Ordinal)
                || normalized.Contains("/../", StringComparison.Ordinal)
                || Path.IsPathRooted(raw))
                continue;

            result.Add(normalized);
        }

        return result.ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeManifestPath(string value)
        => value.Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsStandardStrategyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            var fileName = Path.GetFileName(value.Trim());
            if (!fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                fileName += ".bat";
            return StandardStrategyFile.IsMatch(fileName);
        }
        catch
        {
            return false;
        }
    }

    private static string? StrategyLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!IsStandardStrategyName(value))
            return "пользовательская стратегия (имя скрыто)";

        var fileName = Path.GetFileName(value.Trim());
        return fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }

    private static bool IsSafeManagedAreaFile(
        string area,
        string fileName) =>
        area.Equals("bin", StringComparison.OrdinalIgnoreCase)
            ? SafeBinFileNames.Contains(fileName)
            : area.Equals("utils", StringComparison.OrdinalIgnoreCase)
                && SafeUtilsFileNames.Contains(fileName);

    private static string? SafeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > 64)
            return null;

        return trimmed.All(static c =>
            char.IsAsciiLetterOrDigit(c)
            || c is '.' or '-' or '_' or '+' or ' ')
            ? trimmed
            : null;
    }

    private static string[] SensitiveValues(
        string? telegramSecret,
        IEnumerable<string?> additionalValues)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddSensitive(values, telegramSecret);
        AddSensitive(values, Environment.UserName);
        AddSensitive(values, Environment.UserDomainName);
        AddSensitive(values, Environment.MachineName);
        AddSensitive(values, Environment.GetEnvironmentVariable("USERNAME"));
        AddSensitive(values, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddSensitive(values, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddSensitive(values, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddSensitive(values, Path.GetTempPath());
        AddSensitive(values, Environment.ProcessPath);
        AddSensitive(values, AppContext.BaseDirectory);
        AddSensitive(values, AppPaths.Root);
        AddSensitive(values, AppPaths.DataDir);
        foreach (var value in additionalValues)
            AddSensitive(values, value);

        return values
            .OrderByDescending(static value => value.Length)
            .ToArray();
    }

    private static void AddSensitive(ISet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 2)
            values.Add(value.Trim().TrimEnd('\\', '/'));
    }

    private static void AppendTrusted(StringBuilder sb, string name, string value)
        => sb.Append(name).Append(": ").AppendLine(value);

    private static void AppendRedacted(
        StringBuilder sb,
        string name,
        string value,
        ReportRedactor redactor)
        => sb.Append(name).Append(": ").AppendLine(redactor.Clean(value));

    private static string YesNo(bool value) => value ? "да" : "нет";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Временный файл будет удалён системой/пользователем позже.
        }
    }

    private sealed class ReportRedactor
    {
        private const int MaxTextLength = 4_000;

        private static readonly Regex Url = new(
            @"(?i)\b(?:https?|tg)://[^\s<>]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Email = new(
            @"(?i)(?<![\w.+-])[\w.+-]+@[\w-]+(?:\.[\w-]+)+(?![\w.-])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Credential = new(
            @"(?i)\b(secret|token|password|passwd|api[_-]?key|authorization)\b(?:\s*[:=]\s*|\s+)[^\s,;]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex WindowsPath = new(
            @"(?i)(?<![\p{L}\p{N}_])(?:[a-z]:[\\/]|\\\\)[^\r\n<>|]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex UnixHomePath = new(
            @"(?i)(?<![:\p{L}\p{N}_])/(?:users|home|tmp|var|opt)/[^\r\n<>|]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Ipv4 = new(
            @"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?(?![\d.])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex IpCandidate = new(
            @"(?i)(?<![0-9a-z])(?:\[[0-9a-f:.%]+\]|[0-9a-f:.]*:[0-9a-f:.%]+)(?:/\d{1,3})?(?![0-9a-z])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LongHex = new(
            @"(?i)(?<![0-9a-f])[0-9a-f]{32,}(?![0-9a-f])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LongToken = new(
            @"(?<![\p{L}\p{N}_-])[\p{L}\p{N}_-]{40,}(?![\p{L}\p{N}_-])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly string[] _sensitiveValues;

        public ReportRedactor(string[] sensitiveValues)
        {
            _sensitiveValues = sensitiveValues;
        }

        public string Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "—";

            var text = value.Replace('\0', ' ').Trim();

            foreach (var sensitive in _sensitiveValues)
            {
                text = text.Replace(
                    sensitive,
                    SensitiveMarker(sensitive),
                    StringComparison.OrdinalIgnoreCase);
            }

            text = Credential.Replace(text, static match => match.Groups[1].Value + "=<redacted>");
            text = Url.Replace(text, "<url>");
            text = Email.Replace(text, "<email>");
            text = WindowsPath.Replace(text, "<local-path>");
            text = UnixHomePath.Replace(text, "<local-path>");
            text = Ipv4.Replace(text, "<ip>");
            text = IpCandidate.Replace(text, RedactIpCandidate);
            text = LongHex.Replace(text, "<secret>");
            text = LongToken.Replace(text, "<token>");

            text = SingleLine(text);
            return text.Length <= MaxTextLength
                ? text
                : text[..MaxTextLength] + "…";
        }

        public string CleanFileName(string fileName)
        {
            var cleaned = fileName;
            foreach (var sensitive in _sensitiveValues)
            {
                if (sensitive.IndexOfAny(new[] { '\\', '/' }) >= 0)
                    continue;

                cleaned = cleaned.Replace(
                    sensitive,
                    "<redacted>",
                    StringComparison.OrdinalIgnoreCase);
            }

            cleaned = Ipv4.Replace(cleaned, "<ip>");
            cleaned = LongHex.Replace(cleaned, "<secret>");
            return SingleLine(cleaned);
        }

        private static string SensitiveMarker(string value)
        {
            if (value.Length >= 24 && value.All(static c => char.IsAsciiHexDigit(c)))
                return "<secret>";
            if (value.IndexOfAny(new[] { '\\', '/' }) >= 0)
                return "<local-path>";
            return "<redacted>";
        }

        private static string RedactIpCandidate(Match match)
        {
            var candidate = match.Value;
            var slash = candidate.LastIndexOf('/');
            if (slash > 0)
                candidate = candidate[..slash];

            candidate = candidate.Trim('[', ']');
            var zone = candidate.LastIndexOf('%');
            if (zone > 0)
                candidate = candidate[..zone];

            return IPAddress.TryParse(candidate, out _)
                ? "<ip>"
                : match.Value;
        }

        private static string SingleLine(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c is '\r' or '\n' or '\t')
                {
                    if (sb.Length == 0 || sb[^1] != ' ')
                        sb.Append(' ');
                    continue;
                }

                if (!char.IsControl(c))
                    sb.Append(c);
            }

            return sb.ToString().Trim();
        }
    }

    private sealed record Snapshot(
        DateTimeOffset CreatedUtc,
        string GuiVersion,
        string AssemblyVersion,
        string? UpdateAvailableVersion,
        string BypassState,
        string ServiceState,
        string? SelectedStrategy,
        string? InstalledServiceStrategy,
        string GameFilter,
        string IpsetMode,
        bool IsApplyingStrategy,
        bool IsDiagnosticsRunning,
        int StrategyCount,
        bool RootValid,
        string TelegramState,
        bool TelegramExecutableFound,
        bool TelegramAutoStart,
        bool IsAdministrator,
        SettingsSnapshot Settings,
        DiagnosticSnapshot[] Diagnostics,
        ProbeSnapshot[] Probes,
        LogSnapshot[] Log,
        string[] SensitiveValues);

    private readonly record struct SettingsSnapshot(
        bool AutoStartBypass,
        bool StartWithWindows,
        bool StartMinimized,
        bool MinimizeToTray,
        bool CloseToTray,
        bool CheckUpdatesOnLaunch,
        bool ReducedMotion,
        bool AutoRestartOnCrash);

    private readonly record struct DiagnosticSnapshot(
        string Id,
        string Title,
        string Status,
        string Detail,
        bool HasFix);

    private readonly record struct ProbeSnapshot(
        string Name,
        bool Ok,
        int LatencyMs,
        string? Error);

    private readonly record struct LogSnapshot(
        DateTime Time,
        string Level,
        string Text);

    private readonly record struct FileSnapshot(
        string RelativeName,
        bool Exists,
        long? Size,
        string? Version);

    private readonly record struct PackageVersions(
        string GuiVersion,
        string UpstreamVersion,
        string Tag,
        string UpstreamCommit,
        string ForkCommit,
        string[] ManagedFiles);
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZapretGui.Core;

public sealed class PortablePackageManifest
{
    public int SchemaVersion { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string GuiVersion { get; set; } = string.Empty;
    public string UpstreamVersion { get; set; } = string.Empty;
    public string UpstreamCommit { get; set; } = string.Empty;
    public string ForkCommit { get; set; } = string.Empty;
    public string PackageRoot { get; set; } = string.Empty;
    public List<PortablePackageFile> Files { get; set; } = [];
}

public sealed class PortablePackageFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class PortableUpdatePlan
{
    public int SchemaVersion { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string GuiVersion { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public string TargetRoot { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;
    public string HelperPath { get; set; } = string.Empty;
    public string MarkerPath { get; set; } = string.Empty;
    public string PackageManifestSha256 { get; set; } = string.Empty;
    public int OriginalProcessId { get; set; }
    public bool WasServiceRunning { get; set; }
}

/// <summary>
/// Проверка portable-пакета и транзакционная side-by-side замена установки.
/// Helper запускается из отдельной защищённой папки на том же томе, поэтому может
/// атомарно заменить работающий single-file EXE только после завершения основного процесса.
/// </summary>
public static class PortableUpdateInstaller
{
    public const string ManifestFileName = "UPDATE_MANIFEST.json";

    private const string ServiceName = "zapret";
    private const string IpsetSentinel = "203.0.113.113/32";
    // В ZIP есть ещё сам UPDATE_MANIFEST.json, а распаковщик допускает
    // не более 10 000 записей целиком.
    private const int MaxManifestFiles = 9_999;
    private const string UpdateMutexName =
        @"Global\ZapretGUI.Update.Apply";
    private const string UpdateReadyEventPrefix =
        @"Local\ZapretGUI.Update.Ready.";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.General)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

    public static async Task WritePlanAsync(
        string planPath,
        PortableUpdatePlan plan,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(plan, JsonOptions);
        await File.WriteAllTextAsync(
            planPath,
            json,
            new UTF8Encoding(false),
            ct).ConfigureAwait(false);
    }

    public static async Task<PortablePackageManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(manifestPath))
            throw new InvalidDataException(
                $"В пакете отсутствует {ManifestFileName}.");

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Размер манифеста пакета некорректен.");

        var manifest = await JsonSerializer.DeserializeAsync<PortablePackageManifest>(
            stream,
            JsonOptions,
            ct).ConfigureAwait(false);
        return manifest ??
               throw new InvalidDataException("Манифест пакета не читается.");
    }

    public static async Task ValidatePackageAsync(
        string packageRoot,
        PortablePackageManifest manifest,
        string expectedGuiVersion,
        string expectedUpstreamVersion,
        string? expectedTag = null,
        CancellationToken ct = default,
        bool enforcePackageRootName = true)
    {
        var root = NormalizeDirectory(packageRoot);
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException(
                $"Версия манифеста {manifest.SchemaVersion} не поддерживается.");
        if (!VersionPolicy.TryParse(expectedGuiVersion, out _) ||
            !VersionPolicy.TryParse(manifest.GuiVersion, out _))
            throw new InvalidDataException(
                "Версия GUI в манифесте должна иметь числовой формат x.y.z.");
        if (!string.Equals(
                manifest.GuiVersion,
                expectedGuiVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.UpstreamVersion,
                expectedUpstreamVersion,
                StringComparison.Ordinal))
            throw new InvalidDataException("Версии манифеста не совпали с релизом.");
        if (!string.IsNullOrWhiteSpace(expectedTag) &&
            !string.Equals(manifest.Tag, expectedTag, StringComparison.Ordinal))
            throw new InvalidDataException("Тег манифеста не совпал с релизом.");
        if (!IsFullCommit(manifest.UpstreamCommit) ||
            !IsFullCommit(manifest.ForkCommit))
            throw new InvalidDataException(
                "Манифест не содержит полные commit SHA.");
        if (enforcePackageRootName &&
            !string.Equals(
                manifest.PackageRoot,
                Path.GetFileName(root),
                StringComparison.Ordinal))
            throw new InvalidDataException("Корневая папка пакета не совпала с манифестом.");
        if (manifest.Files.Count is 0 or > MaxManifestFiles)
            throw new InvalidDataException("Список файлов манифеста некорректен.");

        var declared = new Dictionary<string, PortablePackageFile>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(item.Path);
            if (IsProtectedUserPath(relative))
                throw new InvalidDataException(
                    "Пакет пытается заменить пользовательский файл: " + relative);
            if (item.Size < 0 ||
                item.Sha256.Length != 64 ||
                !item.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException(
                    "Некорректная запись файла в манифесте: " + relative);
            if (!declared.TryAdd(relative, item))
                throw new InvalidDataException(
                    "Файл повторяется в манифесте: " + relative);
        }

        foreach (var required in new[]
                 {
                     "ZapretGUI.exe",
                     @"bin\winws.exe",
                     "service.bat"
                 })
        {
            if (!declared.ContainsKey(required))
                throw new InvalidDataException(
                    "В манифесте отсутствует обязательный файл: " + required);
        }

        var actualFiles = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Where(path => !string.Equals(
                path,
                ManifestFileName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (actualFiles.Length != declared.Count)
            throw new InvalidDataException(
                "Количество файлов пакета не совпало с манифестом.");

        foreach (var relativeRaw in actualFiles)
        {
            ct.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(relativeRaw);
            if (!declared.TryGetValue(relative, out var expected))
                throw new InvalidDataException(
                    "Пакет содержит незаявленный файл: " + relative);

            var fullPath = SafeCombine(root, relative);
            var info = new FileInfo(fullPath);
            if (info.Length != expected.Size)
                throw new InvalidDataException(
                    "Размер файла не совпал с манифестом: " + relative);
            var hash = await ComputeFileSha256Async(fullPath, ct).ConfigureAwait(false);
            if (!FixedHashEquals(hash, expected.Sha256))
                throw new InvalidDataException(
                    "SHA-256 файла не совпал с манифестом: " + relative);
        }
    }

    public static bool LaunchHelper(
        string planPath,
        string expectedPlanSha256,
        out string? error)
    {
        error = null;
        try
        {
            ValidateSha256(expectedPlanSha256, "SHA-256 плана");
            var planDirectory = Path.GetDirectoryName(Path.GetFullPath(planPath)) ??
                                throw new InvalidDataException(
                                    "Не найдена папка плана обновления.");
            var helperPath = Path.Combine(
                planDirectory,
                "ZapretGUI.UpdateHelper.exe");
            if (!File.Exists(helperPath))
                throw new FileNotFoundException(
                    "Не найден временный helper обновления.",
                    helperPath);

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) ||
                !File.Exists(currentExe))
                throw new InvalidDataException(
                    "Не найден текущий исполняемый файл.");

            using var currentStream = new FileStream(
                currentExe,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var helperStream = new FileStream(
                helperPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (!FixedHashEquals(
                    HashStream(currentStream),
                    HashStream(helperStream)))
                throw new InvalidDataException(
                    "Целостность update-helper не подтверждена.");

            var start = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = true,
                WorkingDirectory = planDirectory
            };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(planPath);
            start.ArgumentList.Add(expectedPlanSha256);

            using var ready = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                GetReadyEventName(expectedPlanSha256));
            using var helper = Process.Start(start) ??
                               throw new InvalidOperationException(
                                   "Не удалось запустить update-helper.");
            if (!ready.WaitOne(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill(entireProcessTree: true);
                        helper.WaitForExit(5000);
                    }
                }
                catch
                {
                    // Ни один файл ещё не заменён: основной процесс всё ещё работает.
                }
                throw new TimeoutException(
                    helper.HasExited
                        ? "Update-helper завершился до начала установки."
                        : "Update-helper не подтвердил блокировку повторного запуска.");
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsHelperInvocation(IReadOnlyList<string> args) =>
        args.Count == 3 &&
        args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetPostUpdatePlan(
        IReadOnlyList<string> args,
        out string? planPath,
        out string? expectedPlanSha256)
    {
        planPath = null;
        expectedPlanSha256 = null;
        if (args.Count != 3 ||
            !args[0].Equals("--post-update", StringComparison.OrdinalIgnoreCase))
            return false;
        planPath = args[1];
        expectedPlanSha256 = args[2];
        return true;
    }

    public static bool TryGetRollbackPlan(
        IReadOnlyList<string> args,
        out string? planPath,
        out string? expectedPlanSha256)
    {
        planPath = null;
        expectedPlanSha256 = null;
        if (args.Count != 3 ||
            !args[0].Equals(
                "--rollback-update",
                StringComparison.OrdinalIgnoreCase))
            return false;
        planPath = args[1];
        expectedPlanSha256 = args[2];
        return true;
    }

    public static bool IsTrustedPostUpdateInvocation(
        string planPath,
        string expectedPlanSha256)
    {
        try
        {
            var plan = ReadVerifiedPlan(planPath, expectedPlanSha256);
            ValidateHealthPlan(plan, planPath);
            return true;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
            return false;
        }
    }

    public static bool IsTrustedRollbackInvocation(
        string planPath,
        string expectedPlanSha256)
    {
        try
        {
            var plan = ReadVerifiedPlan(planPath, expectedPlanSha256);
            ValidatePlan(plan, planPath);
            return string.Equals(
                NormalizeDirectory(plan.TargetRoot),
                NormalizeDirectory(AppPaths.Root),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
            return false;
        }
    }

    public static bool IsApplyInProgress()
    {
        using var updateMutex = new Mutex(
            initiallyOwned: false,
            name: UpdateMutexName);
        var held = false;
        try
        {
            try
            {
                held = updateMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            return !held;
        }
        finally
        {
            if (held)
            {
                try { updateMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    public static void MarkHealthy(
        string planPath,
        string expectedPlanSha256)
    {
        try
        {
            var plan = ReadVerifiedPlan(planPath, expectedPlanSha256);
            ValidateHealthPlan(plan, planPath);
            var markerDirectory = Path.GetDirectoryName(plan.MarkerPath);
            if (!string.IsNullOrWhiteSpace(markerDirectory))
                Directory.CreateDirectory(markerDirectory);
            File.WriteAllText(
                plan.MarkerPath,
                $"{DateTimeOffset.UtcNow:O}\n{UpdateService.LocalVersion}\n",
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }
    }

    public static int RunHelper(
        string planPath,
        string expectedPlanSha256)
    {
        using var updateMutex = new Mutex(
            initiallyOwned: false,
            name: UpdateMutexName);
        bool mutexHeld;
        try
        {
            mutexHeld = updateMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            mutexHeld = true;
        }

        if (!mutexHeld)
            return 2;

        try
        {
            SignalHelperReady(expectedPlanSha256);
            return RunHelperCore(planPath, expectedPlanSha256);
        }
        finally
        {
            try { updateMutex.ReleaseMutex(); } catch { }
        }
    }

    private static int RunHelperCore(
        string planPath,
        string expectedPlanSha256)
    {
        PortableUpdatePlan? plan = null;
        string? stagingRoot = null;
        string? failedRoot = null;
        Process? newProcess = null;
        string? portableRootSecurity = null;
        var swapped = false;
        var filesRestored = false;
        var serviceRestored = false;
        var restoreServiceAfterTransaction = false;
        var serviceStoppedByHelper = false;
        try
        {
            plan = ReadVerifiedPlan(planPath, expectedPlanSha256);
            ValidatePlan(plan, planPath);
            WaitForProcessExit(plan.OriginalProcessId, TimeSpan.FromSeconds(40));
            portableRootSecurity =
                SecureUpdateDirectory.CapturePortableRootSecurity(
                    plan.TargetRoot);

            var manifestPath = Path.Combine(
                plan.SourceRoot,
                ManifestFileName);
            if (!FixedHashEquals(
                    HashFile(manifestPath),
                    plan.PackageManifestSha256))
                throw new InvalidDataException(
                    "Манифест пакета изменился после проверки.");
            var manifest = ReadManifestAsync(manifestPath)
                .GetAwaiter()
                .GetResult();
            ValidatePackageAsync(
                    plan.SourceRoot,
                    manifest,
                    plan.GuiVersion,
                    manifest.UpstreamVersion,
                    plan.Tag)
                .GetAwaiter()
                .GetResult();

            var planDirectory = Path.GetDirectoryName(
                Path.GetFullPath(planPath))!;
            stagingRoot = SecureUpdateDirectory.CreateUniqueChild(
                planDirectory,
                "staging-");
            CopyPackage(plan.SourceRoot, stagingRoot, manifest);
            ValidatePackageAsync(
                    stagingRoot,
                    manifest,
                    plan.GuiVersion,
                    manifest.UpstreamVersion,
                    plan.Tag,
                    enforcePackageRootName: false)
                .GetAwaiter()
                .GetResult();
            PreserveUserState(plan.TargetRoot, stagingRoot, manifest);

            if (Directory.Exists(plan.BackupRoot))
                throw new IOException(
                    "Папка резервной копии уже существует: " + plan.BackupRoot);

            var serviceState = WaitForStableServiceState(
                TimeSpan.FromSeconds(15));
            if (serviceState is ServiceState.Pending or ServiceState.Unknown)
                throw new InvalidOperationException(
                    "Служба zapret не перешла в стабильное состояние — обновление отменено.");
            restoreServiceAfterTransaction =
                serviceState == ServiceState.Running;
            if (restoreServiceAfterTransaction)
            {
                if (!StopService())
                    throw new InvalidOperationException(
                        "Не удалось остановить службу zapret перед обновлением.");
                serviceStoppedByHelper = true;
            }

            Directory.Move(plan.TargetRoot, plan.BackupRoot);
            try
            {
                Directory.Move(stagingRoot, plan.TargetRoot);
                stagingRoot = null;
                swapped = true;
            }
            catch
            {
                Directory.Move(plan.BackupRoot, plan.TargetRoot);
                throw;
            }

            TryDeleteFile(plan.MarkerPath);
            var newExe = Path.Combine(plan.TargetRoot, "ZapretGUI.exe");
            var start = new ProcessStartInfo
            {
                FileName = newExe,
                UseShellExecute = true,
                WorkingDirectory = plan.TargetRoot
            };
            start.ArgumentList.Add("--post-update");
            start.ArgumentList.Add(planPath);
            start.ArgumentList.Add(expectedPlanSha256);
            newProcess = Process.Start(start) ??
                         throw new InvalidOperationException(
                             "Не удалось запустить обновлённое приложение.");

            if (!WaitForHealthMarker(
                    plan.MarkerPath,
                    newProcess,
                    TimeSpan.FromSeconds(60)))
                throw new InvalidOperationException(
                    "Новая версия не подтвердила успешный запуск.");

            if (restoreServiceAfterTransaction)
            {
                if (!StartService())
                    throw new InvalidOperationException(
                        "Новая версия запустилась, но служба zapret не восстановилась.");
                serviceStoppedByHelper = false;
            }

            SecureUpdateDirectory.RestorePortableTreeSecurity(
                plan.TargetRoot,
                portableRootSecurity);
            return 0;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: true);
            try
            {
                if (newProcess is { HasExited: false })
                {
                    newProcess.Kill(entireProcessTree: true);
                    newProcess.WaitForExit(5000);
                }
            }
            catch
            {
                // Продолжаем откат.
            }

            filesRestored = plan is not null &&
                            !swapped &&
                            Directory.Exists(plan.TargetRoot);
            serviceRestored = plan is not null &&
                              !restoreServiceAfterTransaction;

            if (plan is not null &&
                !swapped &&
                restoreServiceAfterTransaction)
            {
                serviceRestored = !serviceStoppedByHelper
                    ? WaitForStableServiceState(TimeSpan.FromSeconds(5)) ==
                      ServiceState.Running
                    : StartService();
            }

            if (plan is not null && swapped)
            {
                try
                {
                    if (restoreServiceAfterTransaction && !StopService())
                        throw new InvalidOperationException(
                            "Не удалось остановить службу перед откатом.");
                    failedRoot = plan.TargetRoot + ".failed-" +
                                 DateTime.UtcNow.ToString("yyyyMMddHHmmss") +
                                 "-" +
                                 Guid.NewGuid().ToString("N")[..6];
                    if (Directory.Exists(plan.TargetRoot))
                        Directory.Move(plan.TargetRoot, failedRoot);
                    if (Directory.Exists(plan.BackupRoot))
                        Directory.Move(plan.BackupRoot, plan.TargetRoot);
                    filesRestored = Directory.Exists(plan.TargetRoot) &&
                                    File.Exists(Path.Combine(
                                        plan.TargetRoot,
                                        "ZapretGUI.exe"));
                    serviceRestored = !restoreServiceAfterTransaction ||
                                      StartService();
                }
                catch (Exception rollbackError)
                {
                    App.WriteCrashLog(rollbackError, fatal: true);
                }
            }

            if (plan is not null &&
                filesRestored &&
                Directory.Exists(plan.TargetRoot))
            {
                try
                {
                    var oldExe = Path.Combine(plan.TargetRoot, "ZapretGUI.exe");
                    if (File.Exists(oldExe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = oldExe,
                            UseShellExecute = true,
                            WorkingDirectory = plan.TargetRoot,
                            ArgumentList =
                            {
                                "--rollback-update",
                                planPath,
                                expectedPlanSha256
                            }
                        });
                    }
                }
                catch
                {
                    // Пользователь всё равно увидит MessageBox helper-а.
                }
            }

            var rollbackStatus = filesRestored && serviceRestored
                ? "Предыдущая версия и состояние службы восстановлены."
                : filesRestored
                    ? "Файлы предыдущей версии восстановлены, но службу нужно проверить вручную."
                    : "Автоматический откат не завершился. Не перемещайте папки и восстановите backup вручную.";
            var backupHint = plan is null
                ? string.Empty
                : "\n\nРезервная папка:\n" + plan.BackupRoot;
            System.Windows.MessageBox.Show(
                "Обновление не было применено.\n\n" +
                rollbackStatus +
                "\n\nПричина: " +
                ex.Message +
                (failedRoot is null
                    ? string.Empty
                    : "\n\nНеудачная новая сборка:\n" + failedRoot) +
                backupHint,
                filesRestored
                    ? "Zapret Control Center — выполнен откат"
                    : "Zapret Control Center — требуется ручное восстановление",
                System.Windows.MessageBoxButton.OK,
                filesRestored
                    ? System.Windows.MessageBoxImage.Warning
                    : System.Windows.MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            newProcess?.Dispose();
            if (stagingRoot is not null)
                TryDeleteDirectory(stagingRoot);
        }
    }

    private static PortableUpdatePlan ReadVerifiedPlan(
        string planPath,
        string expectedPlanSha256)
    {
        ValidateSha256(expectedPlanSha256, "SHA-256 плана");
        var bytes = File.ReadAllBytes(planPath);
        var actual = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        if (!FixedHashEquals(actual, expectedPlanSha256))
            throw new InvalidDataException(
                "План обновления изменился после подтверждения.");
        return JsonSerializer.Deserialize<PortableUpdatePlan>(bytes, JsonOptions) ??
               throw new InvalidDataException("План обновления не читается.");
    }

    private static void ValidatePlan(
        PortableUpdatePlan plan,
        string planPath)
    {
        if (plan.SchemaVersion != 1)
            throw new InvalidDataException("Версия плана обновления не поддерживается.");
        if (plan.OriginalProcessId <= 0)
            throw new InvalidDataException("PID исходного приложения некорректен.");

        var planFull = Path.GetFullPath(planPath);
        var updatesFull = NormalizeDirectory(AppPaths.UpdatesDir) +
                          Path.DirectorySeparatorChar;
        if (!planFull.StartsWith(updatesFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "План обновления находится вне защищённой временной папки.");

        plan.SourceRoot = NormalizeDirectory(plan.SourceRoot);
        plan.TargetRoot = NormalizeDirectory(plan.TargetRoot);
        plan.BackupRoot = NormalizeDirectory(plan.BackupRoot);
        plan.HelperPath = Path.GetFullPath(plan.HelperPath);
        plan.MarkerPath = Path.GetFullPath(plan.MarkerPath);
        ValidateSha256(
            plan.PackageManifestSha256,
            "SHA-256 манифеста пакета");

        if (!Directory.Exists(plan.SourceRoot) ||
            !Directory.Exists(plan.TargetRoot))
            throw new DirectoryNotFoundException(
                "Исходная или целевая папка обновления не найдена.");
        if (!File.Exists(Path.Combine(plan.TargetRoot, "ZapretGUI.exe")) ||
            !File.Exists(Path.Combine(plan.TargetRoot, "bin", "winws.exe")) ||
            !File.Exists(Path.Combine(plan.TargetRoot, "service.bat")))
            throw new InvalidDataException(
                "Целевая папка не похожа на portable-установку zapret.");
        if (Directory.Exists(Path.Combine(plan.TargetRoot, ".git")))
            throw new InvalidOperationException(
                "Автообновление отключено для git-репозитория. Используйте git pull.");

        var planDirectory = NormalizeDirectory(
            Path.GetDirectoryName(planFull) ??
            throw new InvalidDataException("Не найдена папка плана обновления."));
        SecureUpdateDirectory.Validate(AppPaths.UpdatesDir);
        SecureUpdateDirectory.Validate(planDirectory);
        var planPrefix = planDirectory + Path.DirectorySeparatorChar;
        if (!plan.SourceRoot.StartsWith(
                planPrefix,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Распакованный пакет находится вне папки плана.");
        if (!string.Equals(
                plan.HelperPath,
                Path.Combine(planDirectory, "ZapretGUI.UpdateHelper.exe"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                plan.MarkerPath,
                Path.Combine(planDirectory, "healthy.marker"),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Служебные пути плана обновления некорректны.");

        var parent = Directory.GetParent(plan.TargetRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) ||
            string.Equals(
                Path.GetPathRoot(plan.TargetRoot)?.TrimEnd('\\'),
                plan.TargetRoot.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Корень диска нельзя обновлять как portable-папку.");

        if (!string.Equals(
                NormalizeDirectory(
                    Path.GetDirectoryName(plan.BackupRoot) ?? string.Empty),
                NormalizeDirectory(parent),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                plan.BackupRoot,
                plan.TargetRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Резервная копия должна находиться рядом с установкой.");
    }

    private static void ValidateHealthPlan(
        PortableUpdatePlan plan,
        string planPath)
    {
        var planDirectory = NormalizeDirectory(
            Path.GetDirectoryName(Path.GetFullPath(planPath)) ??
            throw new InvalidDataException("Не найдена папка плана обновления."));
        var updatesPrefix = NormalizeDirectory(AppPaths.UpdatesDir) +
                            Path.DirectorySeparatorChar;
        SecureUpdateDirectory.Validate(AppPaths.UpdatesDir);
        SecureUpdateDirectory.Validate(planDirectory);
        if (!(planDirectory + Path.DirectorySeparatorChar).StartsWith(
                updatesPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                NormalizeDirectory(plan.TargetRoot),
                NormalizeDirectory(AppPaths.Root),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFullPath(plan.MarkerPath),
                Path.Combine(planDirectory, "healthy.marker"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                plan.GuiVersion,
                UpdateService.LocalVersion,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Health-план не соответствует запущенной версии.");
    }

    private static string GetReadyEventName(string expectedPlanSha256)
    {
        ValidateSha256(expectedPlanSha256, "SHA-256 плана");
        return UpdateReadyEventPrefix + expectedPlanSha256.ToLowerInvariant();
    }

    private static void SignalHelperReady(string expectedPlanSha256)
    {
        try
        {
            using var ready = EventWaitHandle.OpenExisting(
                GetReadyEventName(expectedPlanSha256));
            ready.Set();
        }
        catch
        {
            // Helper может быть запущен вручную без ожидающего родительского процесса.
        }
    }

    private static void CopyPackage(
        string sourceRoot,
        string stagingRoot,
        PortablePackageManifest manifest)
    {
        Directory.CreateDirectory(stagingRoot);
        foreach (var item in manifest.Files)
        {
            var relative = NormalizeRelativePath(item.Path);
            var source = SafeCombine(sourceRoot, relative);
            var destination = SafeCombine(stagingRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }

        File.Copy(
            Path.Combine(sourceRoot, ManifestFileName),
            Path.Combine(stagingRoot, ManifestFileName),
            overwrite: false);
    }

    private static void PreserveUserState(
        string oldRoot,
        string stagingRoot,
        PortablePackageManifest newManifest)
    {
        foreach (var relative in new[]
                 {
                     @"lists\list-general-user.txt",
                     @"lists\list-exclude-user.txt",
                     @"lists\ipset-exclude-user.txt",
                     @"utils\game_filter.enabled",
                     @"utils\check_updates.enabled",
                     @"bin\ACTIVE_DISCORD_UDP.bin",
                     @"bin\ACTIVE_GAME_UDP.bin"
                 })
        {
            PreserveOptionalFile(oldRoot, stagingRoot, relative);
        }

        PreserveIpsetMode(oldRoot, stagingRoot);

        var managed = new HashSet<string>(
            newManifest.Files.Select(file => NormalizeRelativePath(file.Path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var oldStrategy in Directory.EnumerateFiles(
                     oldRoot,
                     "general*.bat",
                     SearchOption.TopDirectoryOnly))
        {
            var relative = Path.GetFileName(oldStrategy);
            if (managed.Contains(relative))
                continue;
            var destination = SafeCombine(stagingRoot, relative);
            File.Copy(oldStrategy, destination, overwrite: false);
        }
    }

    private static void PreserveOptionalFile(
        string oldRoot,
        string stagingRoot,
        string relative)
    {
        var oldPath = SafeCombine(oldRoot, relative);
        var newPath = SafeCombine(stagingRoot, relative);
        if (File.Exists(oldPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.Copy(oldPath, newPath, overwrite: true);
        }
        else
        {
            TryDeleteFile(newPath);
        }
    }

    private static void PreserveIpsetMode(
        string oldRoot,
        string stagingRoot)
    {
        var oldIpset = SafeCombine(oldRoot, @"lists\ipset-all.txt");
        if (!File.Exists(oldIpset))
            return;

        var oldText = File.ReadAllText(oldIpset);
        var oldMode = string.IsNullOrWhiteSpace(oldText)
            ? "any"
            : oldText.Contains(IpsetSentinel, StringComparison.Ordinal)
                ? "none"
                : "loaded";

        var newIpset = SafeCombine(stagingRoot, @"lists\ipset-all.txt");
        var newBackup = SafeCombine(stagingRoot, @"lists\ipset-all.txt.backup");
        var actualSource =
            File.Exists(newIpset) &&
            !string.IsNullOrWhiteSpace(File.ReadAllText(newIpset)) &&
            !File.ReadAllText(newIpset).Contains(
                IpsetSentinel,
                StringComparison.Ordinal)
                ? newIpset
                : newBackup;

        if (!File.Exists(actualSource))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(newIpset)!);
        var actualTemp = newBackup + ".update-source";
        File.Copy(actualSource, actualTemp, overwrite: true);

        if (oldMode == "loaded")
        {
            File.Copy(actualTemp, newIpset, overwrite: true);
            TryDeleteFile(newBackup);
        }
        else
        {
            File.Copy(actualTemp, newBackup, overwrite: true);
            File.WriteAllText(
                newIpset,
                oldMode == "none" ? IpsetSentinel + "\r\n" : string.Empty,
                new UTF8Encoding(false));
        }

        TryDeleteFile(actualTemp);
    }

    private static bool IsProtectedUserPath(string relative)
    {
        var name = Path.GetFileName(relative);
        return name.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase) ||
               relative.Equals(
                   @"utils\game_filter.enabled",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException("Путь в манифесте некорректен.");

        var normalized = relative
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var parts = normalized.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 ||
            parts.Any(part => part is "." or ".." ||
                              part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException(
                "Небезопасный путь в манифесте: " + relative);
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static string SafeCombine(string root, string relative)
    {
        var normalizedRoot = NormalizeDirectory(root);
        var full = Path.GetFullPath(
            Path.Combine(normalizedRoot, NormalizeRelativePath(relative)));
        var prefix = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Путь выходит за границы обновляемой папки.");
        return full;
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    internal static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string HashStream(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static void ValidateSha256(string value, string label)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException(label + " имеет неверный формат.");
    }

    private static bool FixedHashEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFullCommit(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static void WaitForProcessExit(
        int processId,
        TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                throw new TimeoutException(
                    "Приложение не завершилось перед обновлением.");
        }
        catch (ArgumentException)
        {
            // Процесс уже завершён.
        }
    }

    private static bool WaitForHealthMarker(
        string markerPath,
        Process process,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(markerPath))
                return true;
            if (process.HasExited)
                return false;
            Thread.Sleep(500);
        }
        return File.Exists(markerPath);
    }

    private static bool StopService()
    {
        RunSc($"stop {ServiceName}", 30_000);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var query = RunSc($"query {ServiceName}", 10_000);
            if (query.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
                query.Contains("1060", StringComparison.Ordinal))
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool StartService()
    {
        RunSc($"start {ServiceName}", 30_000);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var query = RunSc($"query {ServiceName}", 10_000);
            if (query.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static ServiceState WaitForStableServiceState(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        ServiceState state;
        do
        {
            state = QueryServiceState();
            if (state is not ServiceState.Pending and not ServiceState.Unknown)
                return state;
            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        return state;
    }

    private static ServiceState QueryServiceState()
    {
        var query = RunSc($"query {ServiceName}", 10_000);
        if (query.Contains("1060", StringComparison.Ordinal))
            return ServiceState.NotInstalled;
        if (query.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("PAUSE_PENDING", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("CONTINUE_PENDING", StringComparison.OrdinalIgnoreCase))
            return ServiceState.Pending;
        if (query.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            return ServiceState.Running;
        if (query.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("PAUSED", StringComparison.OrdinalIgnoreCase))
            return ServiceState.Stopped;
        return ServiceState.Unknown;
    }

    private static string RunSc(string arguments, int timeoutMs)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "timeout";
            }
            Task.WaitAll([stdout, stderr], 5000);
            return stdout.Result + Environment.NewLine + stderr.Result;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Откат не должен падать из-за необязательного временного файла.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Остаток staging не затрагивает текущую установку.
        }
    }
}

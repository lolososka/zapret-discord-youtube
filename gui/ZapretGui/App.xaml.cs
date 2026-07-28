using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ZapretGui.Core;

namespace ZapretGui;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private static string? _pendingPostUpdatePlan;
    private static string? _pendingPostUpdatePlanSha256;

    /// <summary>Приложение запущено с ключом --minimized (автозапуск с Windows).</summary>
    public static bool StartMinimizedRequested { get; private set; }
    public static bool PostUpdateHealthCheckRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (PortableUpdateInstaller.IsHelperInvocation(e.Args))
        {
            base.OnStartup(e);
            var exitCode = PortableUpdateInstaller.RunHelper(
                e.Args[1],
                e.Args[2]);
            Shutdown(exitCode);
            return;
        }

        if (PortableUpdateInstaller.TryGetPostUpdatePlan(
                e.Args,
                out var postUpdatePlan,
                out var postUpdatePlanSha256))
        {
            if (!string.IsNullOrWhiteSpace(postUpdatePlan) &&
                !string.IsNullOrWhiteSpace(postUpdatePlanSha256) &&
                PortableUpdateInstaller.IsTrustedPostUpdateInvocation(
                    postUpdatePlan,
                    postUpdatePlanSha256))
            {
                _pendingPostUpdatePlan = postUpdatePlan;
                _pendingPostUpdatePlanSha256 = postUpdatePlanSha256;
                PostUpdateHealthCheckRequested = true;
            }
        }

        var trustedRollback =
            PortableUpdateInstaller.TryGetRollbackPlan(
                e.Args,
                out var rollbackPlan,
                out var rollbackPlanSha256) &&
            !string.IsNullOrWhiteSpace(rollbackPlan) &&
            !string.IsNullOrWhiteSpace(rollbackPlanSha256) &&
            PortableUpdateInstaller.IsTrustedRollbackInvocation(
                rollbackPlan,
                rollbackPlanSha256);
        var trustedUpdateChild =
            PostUpdateHealthCheckRequested || trustedRollback;

        if (!trustedUpdateChild &&
            PortableUpdateInstaller.IsApplyInProgress())
        {
            base.OnStartup(e);
            MessageBox.Show(
                "Сейчас устанавливается проверенное обновление. " +
                "Новое окно откроется автоматически после завершения.",
                "Zapret Control Center — обновление",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        StartMinimizedRequested = e.Args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

        // Второй экземпляр держал бы второй winws.exe — это гарантированный конфликт WinDivert.
        _instanceMutex = new Mutex(true, @"Global\ZapretGUI.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            SingleInstance.SignalExistingWindow();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception, fatal: true);

        base.OnStartup(e);

        if (!AppPaths.IsValidRoot)
        {
            MessageBox.Show(
                "Не найден bin\\winws.exe.\n\n" +
                "Положите ZapretGUI.exe в папку zapret — рядом с папками bin, lists и файлами стратегий (*.bat).\n\n" +
                $"Сейчас программа ищет здесь:\n{AppPaths.Root}",
                "Zapret Control Center",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        AppPaths.EnsureUserLists();

        // App.xaml не задаёт StartupUri и менять его нельзя — окно поднимаем отсюда.
        var window = new ZapretGui.MainWindow();
        window.Show();
    }

    /// <summary>
    /// Вызывается только после успешной инициализации главного окна. Helper ждёт этот
    /// marker и автоматически откатывает portable-папку, если новая версия не поднялась.
    /// </summary>
    internal static void MarkPendingUpdateHealthy()
    {
        var planPath = Interlocked.Exchange(
            ref _pendingPostUpdatePlan,
            null);
        var planSha256 = Interlocked.Exchange(
            ref _pendingPostUpdatePlanSha256,
            null);
        if (!string.IsNullOrWhiteSpace(planPath) &&
            !string.IsNullOrWhiteSpace(planSha256))
            PortableUpdateInstaller.MarkHealthy(planPath, planSha256);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* уже освобождён */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, fatal: false);
        e.Handled = true;

        MessageBox.Show(
            "Произошла ошибка, но приложение продолжит работу.\n\n" +
            e.Exception.Message + "\n\n" +
            $"Подробности записаны в:\n{AppPaths.LogFile}",
            "Zapret Control Center",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal static void WriteCrashLog(Exception? ex, bool fatal)
    {
        if (ex is null) return;
        try
        {
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(fatal ? "FATAL" : "ERROR")}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(AppPaths.LogFile, text);
        }
        catch { /* логирование не должно ронять приложение */ }
    }
}

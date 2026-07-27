using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ZapretGui.Core;

namespace ZapretGui;

public partial class App : Application
{
    private static Mutex? _instanceMutex;

    /// <summary>Приложение запущено с ключом --minimized (автозапуск с Windows).</summary>
    public static bool StartMinimizedRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
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

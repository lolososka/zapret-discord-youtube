using System.Threading;

namespace ZapretGui.Core;

/// <summary>
/// Повторный запуск EXE не поднимает второе окно, а будит уже работающее.
/// </summary>
public static class SingleInstance
{
    private const string SignalName = @"Global\ZapretGUI.ShowWindow";

    private static EventWaitHandle? _handle;
    private static RegisteredWaitHandle? _registration;

    /// <summary>Вызывается работающим экземпляром: начать слушать сигнал «покажись».</summary>
    public static void Listen(Action onShowRequested)
    {
        try
        {
            _handle = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _handle,
                (_, _) => onShowRequested(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            // Без сигнального канала просто теряем «поднять окно» — не критично.
        }
    }

    /// <summary>Вызывается вторым экземпляром перед выходом.</summary>
    public static void SignalExistingWindow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SignalName, out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch
        {
            // Нечего будить — выходим молча.
        }
    }

    public static void Stop()
    {
        try
        {
            _registration?.Unregister(null);
            _handle?.Dispose();
        }
        catch { /* завершение работы */ }
        finally
        {
            _registration = null;
            _handle = null;
        }
    }
}

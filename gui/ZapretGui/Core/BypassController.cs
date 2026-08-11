using System.Diagnostics;
using System.Text;

namespace ZapretGui.Core;

public enum BypassState { Stopped, Starting, Running, Stopping, Failed }

public enum LogLevel { Info, Success, Warn, Error }

public sealed record LogLine(DateTime Time, string Text, LogLevel Level);

/// <summary>
/// Владелец дочернего процесса winws.exe. Единственная точка запуска/остановки обхода.
/// </summary>
public sealed class BypassController
{
    private const int HistoryLimit = 2000;
    private const int StartupProbeMs = 2500;   // окно, в котором падение winws.exe считаем ошибкой запуска
    private const int KillWaitMs = 5000;
    private const int MaxAutoRestarts = 3;
    private static readonly TimeSpan AutoRestartWindow = TimeSpan.FromMinutes(10);
    private const long LogFileMaxBytes = 1L * 1024 * 1024;

    private static readonly Lazy<BypassController> LazyInstance =
        new(() => new BypassController(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static BypassController Instance => LazyInstance.Value;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);        // сериализует Start/Stop целиком
    private readonly Queue<LogLine> _history = new(HistoryLimit + 8);
    private readonly StringBuilder _startupOutput = new();
    private readonly object _fileGate = new();

    private Process? _proc;
    private BypassState _state = BypassState.Stopped;
    private Strategy? _activeStrategy;
    private DateTime? _startedAt;
    private volatile bool _captureStartup;
    private bool _logFileChecked;
    private GameFilterMode _lastMode;
    private int _restartAttempts;
    private DateTime _restartWindowStart = DateTime.UtcNow;
    private CancellationTokenSource? _autoRestartCancellation;

    private BypassController()
    {
        // страховка от утечки дочернего процесса при аварийном завершении приложения
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillOwnedProcess();
    }

    // ---------------------------------------------------------------- состояние

    public BypassState State
    {
        get { lock (_gate) return _state; }
    }

    public Strategy? ActiveStrategy
    {
        get { lock (_gate) return _activeStrategy; }
    }

    /// <summary>
    /// Режим игрового фильтра, с которым был запущен текущий процесс. Нужен, чтобы
    /// переключение стратегии могло восстановить прежнюю конфигурацию целиком.
    /// </summary>
    public GameFilterMode ActiveGameFilterMode
    {
        get { lock (_gate) return _lastMode; }
    }

    public DateTime? StartedAt
    {
        get { lock (_gate) return _startedAt; }
    }

    public TimeSpan Uptime
    {
        get
        {
            DateTime? t;
            lock (_gate) t = _startedAt;
            if (t is null) return TimeSpan.Zero;
            var d = DateTime.Now - t.Value;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
    }

    public IReadOnlyList<LogLine> History
    {
        get { lock (_gate) return _history.ToArray(); }
    }

    public event EventHandler<BypassState>? StateChanged;
    public event EventHandler<LogLine>? LogWritten;

    /// <summary>
    /// AppState routes crash recovery through the same operation gate as manual
    /// switching and service changes.
    /// </summary>
    public Func<Strategy, GameFilterMode, CancellationToken, Task>? AutoRestartRequested { get; set; }

    // ---------------------------------------------------------------- запуск

    public async Task<bool> StartAsync(
        Strategy strategy,
        GameFilterMode mode,
        CancellationToken ct = default,
        bool fromAutoRestart = false)
    {
        if (strategy is null)
        {
            Log("Стратегия не выбрана — запуск отменён.", LogLevel.Error);
            return false;
        }

        // Ручной запуск делает ранее запланированное восстановление неактуальным.
        // Сам callback автоперезапуска сохраняет свой token до конца операции.
        if (!fromAutoRestart)
            CancelPendingAutoRestart();

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Повторный запуск гасит только процесс, созданный этим экземпляром GUI.
            // Служебный/сторонний winws.exe нельзя завершать неявно.
            await StopCoreAsync(quiet: true, ownedOnly: true).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (ProcessUtil.IsProcessRunning("winws.exe"))
            {
                lock (_gate)
                {
                    _activeStrategy = null;
                    _startedAt ??= DateTime.Now;
                }
                SetState(BypassState.Running);
                Log(
                    "Обнаружен winws.exe, запущенный службой или другой программой. " +
                    "Запуск отменён: Zapret GUI не завершает чужие процессы.",
                    LogLevel.Warn);
                return false;
            }

            lock (_gate)
            {
                _activeStrategy = strategy;
                _lastMode = mode;
                _startedAt = null;
                _startupOutput.Clear();
            }
            SetState(BypassState.Starting);

            if (!AppPaths.IsValidRoot)
            {
                Log($"Не найден {AppPaths.WinWs}. Поместите ZapretGUI.exe в папку zapret-discord-youtube.", LogLevel.Error);
                FailStart();
                return false;
            }

            AppPaths.EnsureUserLists();
            FeatureFlags.EnableTcpTimestamps();

            string args;
            try
            {
                args = StrategyParser.BuildArguments(strategy, mode);
            }
            catch (Exception ex)
            {
                Log("Не удалось разобрать стратегию: " + ex.Message, LogLevel.Error);
                FailStart();
                return false;
            }

            if (string.IsNullOrWhiteSpace(args))
            {
                Log($"В файле «{strategy.FileName}» не найдена команда запуска winws.exe.", LogLevel.Error);
                FailStart();
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.WinWs,
                Arguments = args,
                WorkingDirectory = AppPaths.Bin,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => OnProcessOutput(e.Data);
            proc.ErrorDataReceived += (_, e) => OnProcessOutput(e.Data);
            proc.Exited += (_, _) => OnProcessExited(proc);

            _captureStartup = true;
            Log($"Запуск стратегии «{strategy.DisplayName}»…");
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!proc.Start())
                {
                    Log("Windows отказалась запускать winws.exe.", LogLevel.Error);
                    SafeDispose(proc);
                    FailStart();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log("Ошибка запуска winws.exe: " + ex.Message, LogLevel.Error);
                SafeDispose(proc);
                FailStart();
                return false;
            }

            lock (_gate) _proc = proc;

            try { proc.BeginOutputReadLine(); } catch { /* поток уже закрыт */ }
            try { proc.BeginErrorReadLine(); } catch { }

            // Ждём StartupProbeMs либо преждевременной смерти процесса. Отмена вызывающей
            // операции должна прервать это окно сразу, а не маскироваться под обычный таймаут.
            using (var startupWindow = new CancellationTokenSource(StartupProbeMs))
            using (var startupWait = CancellationTokenSource.CreateLinkedTokenSource(
                       ct,
                       startupWindow.Token))
            {
                try { await proc.WaitForExitAsync(startupWait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (startupWindow.IsCancellationRequested) { }
                catch (Exception) { }
            }
            ct.ThrowIfCancellationRequested();

            bool exited;
            try { exited = proc.HasExited; } catch { exited = true; }

            if (exited)
            {
                await Task.Delay(250).ConfigureAwait(false);   // даём асинхронным читателям дочитать вывод
                int code;
                try { code = proc.ExitCode; } catch { code = -1; }

                lock (_gate)
                {
                    if (ReferenceEquals(_proc, proc)) _proc = null;
                }
                _captureStartup = false;
                SafeDispose(proc);

                Log($"winws.exe завершился сразу после запуска (код {code}).", LogLevel.Error);

                string tail;
                lock (_gate) tail = _startupOutput.ToString().Trim();
                if (tail.Length > 0)
                {
                    if (tail.Length > 1500) tail = tail[..1500] + "…";
                    Log("Вывод winws.exe: " + tail.Replace("\r\n", " | ").Replace('\n', '|'), LogLevel.Error);
                }
                else
                {
                    Log("Процесс не выдал сообщений. Проверьте, не запущен ли другой обход (GoodbyeDPI, служба zapret) и работает ли WinDivert.", LogLevel.Warn);
                }

                FailStart();
                return false;
            }

            _captureStartup = false;
            lock (_gate)
            {
                _startedAt = DateTime.Now;
                _activeStrategy = strategy;
            }
            SetState(BypassState.Running);
            Log($"Обход работает: «{strategy.DisplayName}»" + GameFilterSuffix(mode) + ".", LogLevel.Success);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log("Запуск обхода отменён.", LogLevel.Info);
            await StopCoreAsync(quiet: true, ownedOnly: true).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            Log("Непредвиденная ошибка запуска: " + ex.Message, LogLevel.Error);
            FailStart();
            return false;
        }
        finally
        {
            _captureStartup = false;
            _mutex.Release();
        }
    }

    private static string GameFilterSuffix(GameFilterMode mode) => mode switch
    {
        GameFilterMode.All => ", игровой фильтр: TCP+UDP",
        GameFilterMode.Tcp => ", игровой фильтр: TCP",
        GameFilterMode.Udp => ", игровой фильтр: UDP",
        _ => "",
    };

    private void FailStart()
    {
        lock (_gate)
        {
            _activeStrategy = null;
            _startedAt = null;
        }
        SetState(BypassState.Failed);
    }

    // ---------------------------------------------------------------- остановка

    /// <param name="ownedOnly">
    /// true — трогаем только свой дочерний процесс. Так закрывается приложение: winws.exe,
    /// поднятый службой zapret или сторонним лаунчером, переживает выход из GUI.
    /// </param>
    public async Task StopAsync(bool ownedOnly = true)
    {
        // Иначе уже запущенный Task.Delay мог снова включить обход после явного Stop.
        CancelPendingAutoRestart();

        // Явная остановка пользователем обнуляет счётчик автоперезапусков.
        lock (_gate) _restartAttempts = 0;

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(quiet: false, ownedOnly: ownedOnly).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("Ошибка при остановке: " + ex.Message, LogLevel.Error);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Вызывается только под _mutex. quiet=true — тихая зачистка перед новым запуском.</summary>
    private async Task StopCoreAsync(bool quiet, bool ownedOnly = true)
    {
        Process? proc;
        lock (_gate)
        {
            proc = _proc;
            _proc = null;   // снимаем «свой» процесс до kill, чтобы Exited не трактовал это как падение
        }

        bool foreign = proc is null && ProcessUtil.IsProcessRunning("winws.exe");

        if (proc is null && !foreign)
        {
            lock (_gate)
            {
                _activeStrategy = null;
                _startedAt = null;
            }
            SetState(BypassState.Stopped);
            return;
        }

        if (proc is null && foreign && ownedOnly)
        {
            lock (_gate)
            {
                _activeStrategy = null;
                _startedAt ??= DateTime.Now;
            }
            SetState(BypassState.Running);
            if (!quiet)
                Log("Чужой или служебный winws.exe оставлен работающим.", LogLevel.Warn);
            return;
        }

        SetState(BypassState.Stopping);

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { /* уже умер или нет прав */ }

            using (var cts = new CancellationTokenSource(KillWaitMs))
            {
                try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch { }
            }
            SafeDispose(proc);
        }

        int killed = ownedOnly ? 0 : ProcessUtil.KillAll("winws.exe");
        bool unownedStillRunning = ownedOnly && ProcessUtil.IsProcessRunning("winws.exe");

        lock (_gate)
        {
            _activeStrategy = null;
            _startedAt = unownedStillRunning ? DateTime.Now : null;
        }
        SetState(unownedStillRunning ? BypassState.Running : BypassState.Stopped);

        if (unownedStillRunning)
        {
            if (!quiet)
                Log("Свой процесс остановлен; чужой или служебный winws.exe продолжает работать.", LogLevel.Warn);
        }
        else if (!quiet)
        {
            Log("Обход остановлен.", LogLevel.Success);
        }
        else if (proc is not null || killed > 0)
        {
            Log("Предыдущий процесс winws.exe остановлен.");
        }
    }

    /// <summary>Синхронное убийство без событий — только для ProcessExit.</summary>
    private void KillOwnedProcess()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _proc;
            _proc = null;
        }
        if (proc is null) return;
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
        }
        catch { }
        SafeDispose(proc);
    }

    /// <summary>
    /// Аварийный синхронный путь закрытия окна. Завершает только дочерний процесс,
    /// созданный этим экземпляром GUI; служебный и сторонний winws.exe не затрагивает.
    /// </summary>
    public void KillOwnedProcessForExit() => KillOwnedProcess();

    // ---------------------------------------------------------------- синхронизация состояния

    public void RefreshState()
    {
        BypassState now = State;
        // не вмешиваемся в идущий переход
        if (now is BypassState.Starting or BypassState.Stopping) return;

        Process? proc;
        lock (_gate) proc = _proc;

        if (proc is not null)
        {
            bool exited;
            try { exited = proc.HasExited; } catch { exited = true; }
            if (!exited)
            {
                SetState(BypassState.Running);
                return;
            }
            HandleUnexpectedExit(proc);
            return;
        }

        // своего процесса нет — winws.exe может быть поднят службой zapret или сторонним лаунчером
        if (ProcessUtil.IsProcessRunning("winws.exe"))
        {
            lock (_gate)
            {
                _activeStrategy = null;
                _startedAt ??= DateTime.Now;
            }
            SetState(BypassState.Running);
            return;
        }

        lock (_gate)
        {
            _activeStrategy = null;
            _startedAt = null;
        }
        if (now != BypassState.Failed) SetState(BypassState.Stopped);
    }

    // ---------------------------------------------------------------- журнал

    public void Log(string text, LogLevel level = LogLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var line = new LogLine(DateTime.Now, text.TrimEnd(), level);

        lock (_gate)
        {
            _history.Enqueue(line);
            while (_history.Count > HistoryLimit) _history.Dequeue();
        }

        AppendToFile(line);
        Post(() => LogWritten?.Invoke(this, line));
    }

    private void OnProcessOutput(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return;
        string text = data.TrimEnd();

        if (_captureStartup)
        {
            lock (_gate)
            {
                if (_startupOutput.Length < 4000) _startupOutput.AppendLine(text);
            }
        }

        Log(text, Classify(text));
    }

    private void OnProcessExited(Process proc)
    {
        HandleUnexpectedExit(proc);
    }

    /// <summary>
    /// Ровно один наблюдатель забирает завершившийся процесс из текущей сессии. Это
    /// исключает двойную обработку между событием Exited, RefreshState и ожидаемым Stop.
    /// </summary>
    private void HandleUnexpectedExit(Process proc)
    {
        Strategy? crashed;
        GameFilterMode mode;
        lock (_gate)
        {
            // StopCoreAsync отвязывает ожидаемо завершаемый процесс под тем же lock.
            // Если другой наблюдатель уже забрал процесс, повторно менять state нельзя.
            if (!ReferenceEquals(_proc, proc) || _captureStartup)
                return;

            _proc = null;
            crashed = _activeStrategy;
            mode = _lastMode;
            _activeStrategy = null;
            _startedAt = null;
        }

        int code;
        try { code = proc.ExitCode; } catch { code = -1; }
        SafeDispose(proc);

        Log($"winws.exe неожиданно завершился (код {code}). Обход больше не работает.", LogLevel.Error);
        SetState(BypassState.Failed);

        if (crashed is not null) _ = TryAutoRestartAsync(crashed, mode);
    }

    /// <summary>
    /// Перезапуск после самопроизвольного падения. Ограничен тремя попытками в окне
    /// AutoRestartWindow — иначе при стойкой ошибке приложение уйдёт в бесконечный цикл.
    /// </summary>
    private async Task TryAutoRestartAsync(Strategy strategy, GameFilterMode mode)
    {
        if (!AppSettings.Current.AutoRestartOnCrash) return;

        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (now - _restartWindowStart > AutoRestartWindow)
            {
                _restartWindowStart = now;
                _restartAttempts = 0;
            }

            if (_restartAttempts >= MaxAutoRestarts)
            {
                Log($"Автоперезапуск не помогает ({MaxAutoRestarts} попытки подряд) — оставляю обход выключенным.",
                    LogLevel.Warn);
                return;
            }

            _restartAttempts++;
        }

        int attempt = _restartAttempts;
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _autoRestartCancellation;
            _autoRestartCancellation = cancellation;
        }
        try { previous?.Cancel(); } catch { }

        Log($"Автоперезапуск обхода, попытка {attempt} из {MaxAutoRestarts}…", LogLevel.Warn);
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(2 * attempt),
                cancellation.Token).ConfigureAwait(false);

            cancellation.Token.ThrowIfCancellationRequested();
            if (State is BypassState.Running or BypassState.Starting) return;   // пользователь успел сам
            if (!AppSettings.Current.AutoRestartOnCrash) return;

            var restart = AutoRestartRequested;
            if (restart is null)
            {
                Log(
                    "Автоперезапуск отменён: координатор операций уже недоступен.",
                    LogLevel.Warn);
                return;
            }
            await restart(strategy, mode, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Log("Отложенный автоперезапуск отменён.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Log("Автоперезапуск завершился ошибкой: " + ex.Message, LogLevel.Error);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_autoRestartCancellation, cancellation))
                    _autoRestartCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelPendingAutoRestart()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _autoRestartCancellation;
            _autoRestartCancellation = null;
        }

        try { cancellation?.Cancel(); } catch { }
    }

    private static LogLevel Classify(string s)
    {
        string l = s.ToLowerInvariant();
        if (l.Contains("error") || l.Contains("fail") || l.Contains("cannot") || l.Contains("unable")) return LogLevel.Error;
        if (l.Contains("warning") || l.Contains("deprecated")) return LogLevel.Warn;
        // «ok» ищем как отдельное слово: иначе окрашиваются «token», «lookup» и подобные
        if (l.Contains("loaded") || l.Contains("success") || l == "ok" || l.EndsWith(" ok") || l.Contains(" ok ")) return LogLevel.Success;
        return LogLevel.Info;
    }

    private void AppendToFile(LogLine line)
    {
        try
        {
            lock (_fileGate)
            {
                string path = AppPaths.LogFile;
                if (!_logFileChecked)
                {
                    _logFileChecked = true;
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > LogFileMaxBytes) fi.Delete();
                }
                char lvl = line.Level switch
                {
                    LogLevel.Error => 'E',
                    LogLevel.Warn => 'W',
                    LogLevel.Success => 'S',
                    _ => 'I',
                };
                File.AppendAllText(path,
                    $"{line.Time:yyyy-MM-dd HH:mm:ss} [{lvl}] {line.Text}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* журнал на диске — не критично */ }
    }

    // ---------------------------------------------------------------- служебное

    private void SetState(BypassState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        Post(() => StateChanged?.Invoke(this, state));
    }

    private static void Post(Action action)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }
            dispatcher.BeginInvoke(action);
        }
        catch { /* приложение закрывается — событие уже никому не нужно */ }
    }

    private static void SafeDispose(Process p)
    {
        try { p.Dispose(); } catch { }
    }
}

using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZapretGui.Controls;
using ZapretGui.Core;
using ZapretGui.Views;

namespace ZapretGui;

public partial class MainWindow : System.Windows.Window
{
    private const double NavPitch = 44.0;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    private readonly Dictionary<string, UIElement> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, RadioButton Button)> _nav = new();

    private TrayIconService? _tray;
    private Storyboard? _statusPulse;
    private WindowState _restoreState = System.Windows.WindowState.Normal;

    private string? _currentKey;
    private int _swapToken;
    private bool _loadedOnce;
    private bool _enterPlayed;
    private bool _startHidden;
    private bool _navigating;
    private bool _pulseRunning;
    private bool _pulsePaused;
    private bool _trayHintShown;
    private bool _reallyExit;
    private bool _closingInProgress;
    private bool _shutdownDone;
    private bool _preparedExitApproved;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = AppState.Instance;

        _startHidden = App.StartMinimizedRequested || AppSettings.Current.StartMinimized;
        // Окно, стартующее в трей, не увидит ContentRendered — оно не должно остаться прозрачным.
        RootBorder.Opacity = _startHidden ? 1 : 0;

        _nav.Add(("dashboard", NavDashboard));
        _nav.Add(("strategies", NavStrategies));
        _nav.Add(("diagnostics", NavDiagnostics));
        _nav.Add(("telegram", NavTelegram));
        _nav.Add(("logs", NavLogs));
        _nav.Add(("settings", NavSettings));

        foreach (var (_, button) in _nav)
            button.Checked += OnNavChecked;

        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        StateChanged += OnWindowStateChanged;
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        KeyDown += OnWindowKeyDown;
    }

    /// <summary>Переход: "dashboard" | "strategies" | "diagnostics" | "telegram" | "logs" | "settings".</summary>
    public void NavigateTo(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var index = _nav.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        var button = _nav[index].Button;
        if (button.IsChecked != true)
        {
            button.IsChecked = true; // Checked поднимет OnNavChecked и выполнит переход
            return;
        }

        ShowPage(index);
    }

    /// <summary>Закрывает приложение по-настоящему после запуска внешнего update-helper.</summary>
    public void ExitForPreparedUpdate()
    {
        _reallyExit = true;
        Close();
    }

    public bool TryApprovePreparedExit()
    {
        if (!ConfirmUnsavedUserLists())
            return false;
        _preparedExitApproved = true;
        return true;
    }

    public void CancelPreparedExitApproval() =>
        _preparedExitApproved = false;

    // ──────────────────────────────────────────────────────── жизненный цикл

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        NativeInterop.ApplyModernWindow(this);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // ContentRendered приходит и после восстановления из трея — вход играем только первый раз.
        if (_enterPlayed)
            return;
        _enterPlayed = true;

        if (RootBorder.Opacity >= 1)
            return;

        PlayWindowEnter();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;

        Fx.DetectReducedMotion();
        ApplyAccent(AppSettings.Current.Accent);

        // Порядок Loaded между окном и дочерним контролом не гарантирован — перечитываем режим движения.
        AmbientLayer.Resume();

        NavigateTo("dashboard");

        AppState.Instance.Notification += OnAppNotification;
        AppState.Instance.PropertyChanged += OnAppStateChanged;

        SingleInstance.Listen(() => Dispatcher.Invoke(RestoreFromTray));

        try
        {
            _tray = new TrayIconService(this);
            _tray.ShowRequested += (_, _) => Dispatcher.Invoke(RestoreFromTray);
            _tray.StartRequested += (_, _) => Dispatcher.Invoke(ToggleBypass);
            _tray.StopRequested += (_, _) => Dispatcher.Invoke(ToggleBypass);
            _tray.ExitRequested += (_, _) => Dispatcher.Invoke(ExitApplication);
            PushTrayState();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }

        if (_startHidden)
            HideToTray(silent: true);

        var initializedSuccessfully = false;
        try
        {
            await AppState.Instance.InitializeAsync();
            initializedSuccessfully = true;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
            AppState.Instance.Notify("Инициализация завершилась с ошибкой — откройте журнал", ToastKind.Error);
        }

        if (initializedSuccessfully)
            App.MarkPendingUpdateHealthy();

        PushTrayState();
        UpdateStatusPulse();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDone)
            return; // повторный проход после завершения работы — выпускаем окно

        e.Cancel = true;

        if (!_reallyExit && AppSettings.Current.CloseToTray && _tray is not null)
        {
            HideToTray(silent: false);
            return;
        }

        if (_closingInProgress)
            return;

        if (!_preparedExitApproved && !ConfirmUnsavedUserLists())
            return;
        _closingInProgress = true;

        StopStatusPulse();

        Task? shutdown = null;
        try
        {
            // winws.exe не должен пережить закрытие: сначала останавливаем обход, потом закрываемся.
            shutdown = AppState.Instance.ShutdownAsync();
            await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(8)));
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }

        if (shutdown is null || !shutdown.IsCompletedSuccessfully)
            KillOwnedBypass();

        SingleInstance.Stop();

        try { _tray?.Dispose(); }
        catch { /* иконка уже снята */ }
        _tray = null;

        _shutdownDone = true;
        Close();
    }

    private bool ConfirmUnsavedUserLists()
    {
        if (!_pages.TryGetValue("strategies", out var page) ||
            page is not StrategiesView strategies)
            return true;
        return strategies.ConfirmDiscardUserListChanges();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        AppState.Instance.Notification -= OnAppNotification;
        AppState.Instance.PropertyChanged -= OnAppStateChanged;

        StopStatusPulse();

        if (!_shutdownDone)
        {
            // Аварийный путь (Application.Shutdown, выход из сеанса) — гарантия из задания.
            SingleInstance.Stop();
            KillOwnedBypass();
            try { _tray?.Dispose(); } catch { }
            _tray = null;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            _restoreState = WindowState;

        GlyphMax.Visibility = WindowState == WindowState.Maximized ? Visibility.Collapsed : Visibility.Visible;
        GlyphRestore.Visibility = WindowState == WindowState.Maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.SetValue(AutomationProperties.NameProperty,
            WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть");

        if (WindowState == WindowState.Minimized)
        {
            PauseAmbience();
            if (AppSettings.Current.MinimizeToTray && _tray is not null)
                HideToTray(silent: false);
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e) => ResumeAmbience();

    private void OnWindowDeactivated(object? sender, EventArgs e) => PauseAmbience();

    // ──────────────────────────────────────────────────────── горячие клавиши

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (ctrl)
        {
            var index = e.Key switch
            {
                Key.D1 or Key.NumPad1 => 0,
                Key.D2 or Key.NumPad2 => 1,
                Key.D3 or Key.NumPad3 => 2,
                Key.D4 or Key.NumPad4 => 3,
                Key.D5 or Key.NumPad5 => 4,
                Key.D6 or Key.NumPad6 => 5,
                _ => -1,
            };

            if (index >= 0 && index < _nav.Count)
            {
                NavigateTo(_nav[index].Key);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.R)
            {
                _ = RestartBypassAsync();
                e.Handled = true;
                return;
            }
        }

        // Esc в поле ввода принадлежит полю, а не окну.
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox)
        {
            HideToTray(silent: false);
            e.Handled = true;
        }
    }

    private void ToggleBypass()
    {
        var command = AppState.Instance.ToggleBypassCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private static async Task RestartBypassAsync()
    {
        var state = AppState.Instance;
        var command = state.ToggleBypassCommand;

        if (state.IsRunning)
        {
            if (!command.CanExecute(null))
                return;

            command.Execute(null);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((state.IsRunning || command.IsRunning) && DateTime.UtcNow < deadline)
                await Task.Delay(120);

            if (state.IsRunning)
                return;
        }

        if (command.CanExecute(null))
            command.Execute(null);
    }

    // ──────────────────────────────────────────────────────── трей

    private void RestoreFromTray()
    {
        try
        {
            if (!IsVisible)
                Show();

            if (WindowState == WindowState.Minimized)
                WindowState = _restoreState == WindowState.Minimized ? WindowState.Normal : _restoreState;

            // Окно, стартовавшее в трей, не проигрывало вход — доводим слои до конечного состояния.
            _enterPlayed = true;
            ResetEnterState();

            Activate();
            Focus();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }
    }

    private void HideToTray(bool silent)
    {
        if (_tray is null)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
        PauseAmbience();

        if (silent || _trayHintShown)
            return;

        _trayHintShown = true;
        _tray.Notify("Zapret Control Center", "Программа свёрнута в трей и продолжает работать");
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        Close();
    }

    private void OnAppNotification(object? sender, (string Message, ToastKind Kind) e)
        => ToastHost.Post(e.Message, e.Kind);

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.IsRunning))
        {
            PushTrayState();
            UpdateStatusPulse();
        }
        else if (e.PropertyName is nameof(AppState.BypassState) or nameof(AppState.SelectedStrategyName))
        {
            PushTrayState();
        }
    }

    /// <summary>Перерисовать значок в трее — например после смены акцента на странице «Настройки».</summary>
    public void RefreshTrayIcon() => PushTrayState();

    private void PushTrayState()
    {
        if (_tray is null)
            return;

        var state = AppState.Instance.BypassState switch
        {
            Core.BypassState.Running => TrayState.Running,
            Core.BypassState.Starting or Core.BypassState.Stopping => TrayState.Starting,
            Core.BypassState.Failed => TrayState.Failed,
            _ => TrayState.Stopped,
        };

        _tray.SetState(state, AppState.Instance.SelectedStrategyName);
    }

    /// <summary>Последний рубеж: свой winws.exe не должен пережить приложение. Служебный — не трогаем.</summary>
    private static void KillOwnedBypass()
    {
        try
        {
            BypassController.Instance.KillOwnedProcessForExit();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }
    }

    // ──────────────────────────────────────────────────────── навигация

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (_navigating)
            return;

        var index = _nav.FindIndex(p => ReferenceEquals(p.Button, sender));
        if (index >= 0)
            ShowPage(index);
    }

    private void ShowPage(int index)
    {
        var (key, button) = _nav[index];
        if (string.Equals(_currentKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        // Поиск exe и опрос процесса — файловые операции, на UI-поток их тащить незачем.
        if (string.Equals(key, "telegram", StringComparison.OrdinalIgnoreCase))
            _ = Task.Run(TelegramProxy.Instance.Refresh);

        _navigating = true;
        try
        {
            button.IsChecked = true;
        }
        finally
        {
            _navigating = false;
        }

        var first = _currentKey is null;
        _currentKey = key;

        MoveIndicator(index, animate: !first);
        SwapContent(GetPage(key), instant: first);
    }

    private UIElement GetPage(string key)
    {
        if (_pages.TryGetValue(key, out var cached))
            return cached;

        var page = CreatePage(key);
        _pages[key] = page;
        return page;
    }

    private UIElement CreatePage(string key) => key switch
    {
        "dashboard" => SafeCreate(() =>
        {
            var view = new DashboardView();
            view.NavigationRequested += (_, target) => NavigateTo(target);
            return view;
        }, "Панель"),

        "strategies" => SafeCreate(() => new StrategiesView(), "Стратегии"),
        "diagnostics" => SafeCreate(() => new DiagnosticsView(), "Диагностика"),

        "telegram" => SafeCreate(() => new TelegramView(), "Телеграм"),
        "logs" => SafeCreate(() => new LogsView(), "Журнал"),
        "settings" => SafeCreate(() => new SettingsView(), "Настройки"),

        _ => BuildPlaceholder("Страница недоступна", "Раздел не найден"),
    };

    private static UIElement SafeCreate(Func<UIElement> factory, string title)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
            return BuildPlaceholder(title + ": страница не открылась", ex.Message);
        }
    }

    private static UIElement BuildPlaceholder(string headline, string detail)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 320,
        };

        stack.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("BrushTextSecondary"),
        });

        stack.Children.Add(new TextBlock
        {
            Text = detail,
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 12.5,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("BrushTextTertiary"),
        });

        return new Grid { Children = { stack } };
    }

    private static Brush Res(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    // ──────────────────────────────────────────────────────── движение

    /// <summary>§7 WindowEnter: корень, подсветка, затем каскад «рейл → контент → марка».</summary>
    private void PlayWindowEnter()
    {
        if (Fx.ReducedMotion)
        {
            ResetEnterState();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        RootBorder.Opacity = 0;
        Animate(RootBorder, OpacityProperty,
            new DoubleAnimation(0, 1, Ms(320)) { EasingFunction = ease }, 1.0);

        RootScale.ScaleX = 0.985;
        RootScale.ScaleY = 0.985;
        Animate(RootScale, ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1.0, Ms(320)) { EasingFunction = ease }, 1.0);
        Animate(RootScale, ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.985, 1.0, Ms(320)) { EasingFunction = ease }, 1.0);

        // Подсветка выходит медленнее окна; собственную непрозрачность слоёв ведёт сам контрол.
        AmbientLayer.Opacity = 0;
        Animate(AmbientLayer, OpacityProperty,
            new DoubleAnimation(0, 1, Ms(900)), 1.0);

        NavRailMove.X = -16;
        Animate(NavRailMove, TranslateTransform.XProperty,
            new DoubleAnimation(-16, 0, Ms(260)) { EasingFunction = ease }, 0.0);

        // Контент идёт следом за рейлом — 80 мс задержки читаются как порядок, а не как лаг.
        PageHost.Opacity = 0;
        PageMove.Y = 10;
        Animate(PageHost, OpacityProperty,
            new DoubleAnimation(0, 1, Ms(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = ease,
            }, 1.0);
        Animate(PageMove, TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, Ms(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = ease,
            }, 0.0);

        TitleMark.Opacity = 0;
        TitleWordmark.Opacity = 0;
        Animate(TitleMark, OpacityProperty,
            new DoubleAnimation(0, 1, Ms(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(140),
                EasingFunction = ease,
            }, 1.0);
        Animate(TitleWordmark, OpacityProperty,
            new DoubleAnimation(0, 1, Ms(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(140),
                EasingFunction = ease,
            }, 1.0);
    }

    /// <summary>Конечное состояние входа без движения — им же чинится окно, поднятое из трея.</summary>
    private void ResetEnterState()
    {
        RootBorder.BeginAnimation(OpacityProperty, null);
        RootBorder.Opacity = 1;

        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootScale.ScaleX = 1;
        RootScale.ScaleY = 1;

        AmbientLayer.BeginAnimation(OpacityProperty, null);
        AmbientLayer.Opacity = 1;

        NavRailMove.BeginAnimation(TranslateTransform.XProperty, null);
        NavRailMove.X = 0;

        PageHost.BeginAnimation(OpacityProperty, null);
        PageHost.Opacity = 1;
        PageMove.BeginAnimation(TranslateTransform.YProperty, null);
        PageMove.Y = 0;

        TitleMark.BeginAnimation(OpacityProperty, null);
        TitleMark.Opacity = 1;
        TitleWordmark.BeginAnimation(OpacityProperty, null);
        TitleWordmark.Opacity = 1;
    }

    private void MoveIndicator(int index, bool animate)
    {
        var to = index * NavPitch;

        if (NavIndicator.Opacity < 1)
            NavIndicator.Opacity = 1;

        if (!animate)
        {
            NavIndicatorMove.BeginAnimation(TranslateTransform.YProperty, null);
            NavIndicatorMove.Y = to;
            return;
        }

        if (Fx.ReducedMotion)
        {
            Animate(NavIndicatorMove, TranslateTransform.YProperty,
                new DoubleAnimation(NavIndicatorMove.Y, to, Ms(120)), to);
            return;
        }

        Animate(NavIndicatorMove, TranslateTransform.YProperty,
            new DoubleAnimation(NavIndicatorMove.Y, to, Ms(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            }, to);

        var squash = new DoubleAnimationUsingKeyFrames { Duration = Ms(220) };
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(0.42, KeyTime.FromPercent(0.45)) { EasingFunction = sine });
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0)) { EasingFunction = sine });
        Animate(NavIndicatorScale, ScaleTransform.ScaleYProperty, squash, 1.0);
    }

    private void SwapContent(UIElement page, bool instant)
    {
        var token = ++_swapToken;

        if (instant)
        {
            PageHost.Content = page;
            PageHost.Opacity = 1;
            PageMove.Y = 0;
            return;
        }

        if (Fx.ReducedMotion)
        {
            Animate(PageHost, OpacityProperty, new DoubleAnimation(PageHost.Opacity, 0, Ms(60)), 0.0, () =>
            {
                if (token != _swapToken) return;
                PageHost.Content = page;
                PageMove.Y = 0;
                Animate(PageHost, OpacityProperty, new DoubleAnimation(0, 1, Ms(120)), 1.0);
            });
            return;
        }

        Animate(PageHost, OpacityProperty,
            new DoubleAnimation(PageHost.Opacity, 0, Ms(90))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            },
            0.0,
            () =>
            {
                if (token != _swapToken)
                    return;

                PageHost.Content = page;
                PageMove.Y = 8;

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Animate(PageHost, OpacityProperty, new DoubleAnimation(0, 1, Ms(180)) { EasingFunction = ease }, 1.0);
                Animate(PageMove, TranslateTransform.YProperty,
                    new DoubleAnimation(8, 0, Ms(180)) { EasingFunction = ease }, 0.0);
            });
    }

    private void UpdateStatusPulse()
    {
        var shouldRun = AppState.Instance.IsRunning && !Fx.ReducedMotion;

        if (!shouldRun)
        {
            StopStatusPulse();
            return;
        }

        if (_pulseRunning)
            return;

        _statusPulse ??= BuildStatusPulse();
        _statusPulse.Begin(this, isControllable: true);
        _pulseRunning = true;
        _pulsePaused = false;

        if (!IsActive)
            PauseAmbience();
    }

    private Storyboard BuildStatusPulse()
    {
        var board = new Storyboard();

        var dot = new DoubleAnimation(1.0, 0.45, Ms(1400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(dot, StatusDot);
        Storyboard.SetTargetProperty(dot, new PropertyPath(OpacityProperty));
        board.Children.Add(dot);

        var haloEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        var haloX = new DoubleAnimation(1.0, 2.4, Ms(1800))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = haloEase,
        };
        Storyboard.SetTarget(haloX, StatusHaloScale);
        Storyboard.SetTargetProperty(haloX, new PropertyPath(ScaleTransform.ScaleXProperty));
        board.Children.Add(haloX);

        var haloY = haloX.Clone();
        Storyboard.SetTarget(haloY, StatusHaloScale);
        Storyboard.SetTargetProperty(haloY, new PropertyPath(ScaleTransform.ScaleYProperty));
        board.Children.Add(haloY);

        var haloFade = new DoubleAnimation(0.50, 0.0, Ms(1800))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = haloEase,
        };
        Storyboard.SetTarget(haloFade, StatusHalo);
        Storyboard.SetTargetProperty(haloFade, new PropertyPath(OpacityProperty));
        board.Children.Add(haloFade);

        return board;
    }

    private void StopStatusPulse()
    {
        if (!_pulseRunning || _statusPulse is null)
            return;

        try
        {
            _statusPulse.Stop(this);
            _statusPulse.Remove(this);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }

        _pulseRunning = false;
        _pulsePaused = false;

        StatusDot.Opacity = 1;
        StatusHalo.Opacity = 0;
        StatusHaloScale.ScaleX = 1;
        StatusHaloScale.ScaleY = 1;
    }

    private void PauseAmbience()
    {
        AmbientLayer.Pause();

        if (_pulseRunning && !_pulsePaused && _statusPulse is not null)
        {
            _statusPulse.Pause(this);
            _pulsePaused = true;
        }
    }

    private void ResumeAmbience()
    {
        AmbientLayer.Resume();

        if (_pulseRunning && _pulsePaused && _statusPulse is not null)
        {
            _statusPulse.Resume(this);
            _pulsePaused = false;
        }
    }

    private static Duration Ms(double value) => new(TimeSpan.FromMilliseconds(value));

    /// <summary>§7: FillBehavior=Stop, конечное значение фиксируется в Completed.</summary>
    private static void Animate(IAnimatable target, DependencyProperty property,
                                AnimationTimeline animation, object finalValue, Action? completed = null)
    {
        animation.FillBehavior = FillBehavior.Stop;

        animation.Completed += (_, _) =>
        {
            try
            {
                target.BeginAnimation(property, null);
                if (target is DependencyObject host)
                    host.SetValue(property, finalValue);
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex, fatal: false);
            }

            completed?.Invoke();
        };

        target.BeginAnimation(property, animation);
    }

    // ──────────────────────────────────────────────────────── акцент

    /// <summary>
    /// Применяет пресет акцента. Если в сборке есть AccentManager (его пишет страница
    /// «Настройки»), источником истины остаётся он — иначе ключи подменяются здесь.
    /// </summary>
    private static void ApplyAccent(string? preset)
    {
        try
        {
            if (TryExternalAccentManager(preset))
                return;

            var resources = Application.Current?.Resources;
            if (resources is null)
                return;

            var (start, mid, end) = PresetColors(preset);

            resources["ColorAccentStart"] = start;
            resources["ColorAccentMid"] = mid;
            resources["ColorAccentEnd"] = end;

            resources["BrushAccentStart"] = Frozen(new SolidColorBrush(start));
            resources["BrushAccentMid"] = Frozen(new SolidColorBrush(mid));
            resources["BrushAccentEnd"] = Frozen(new SolidColorBrush(end));
            resources["BrushAccentGlow"] = Frozen(new SolidColorBrush(WithAlpha(mid, 0x8C)));
            resources["BrushAccentWash"] = Frozen(new SolidColorBrush(WithAlpha(mid, 0x1A)));
            resources["BrushAccentDim"] = Frozen(new SolidColorBrush(WithAlpha(mid, 0x52)));
            resources["BrushStateRunning"] = Frozen(new SolidColorBrush(mid));

            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
            };
            gradient.GradientStops.Add(new GradientStop(start, 0.0));
            gradient.GradientStops.Add(new GradientStop(mid, 0.5));
            gradient.GradientStops.Add(new GradientStop(end, 1.0));
            resources["BrushAccentGradient"] = Frozen(gradient);

            var indicator = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            indicator.GradientStops.Add(new GradientStop(start, 0.0));
            indicator.GradientStops.Add(new GradientStop(end, 1.0));
            resources["BrushNavIndicator"] = Frozen(indicator);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }
    }

    private static bool TryExternalAccentManager(string? preset)
    {
        try
        {
            var assembly = typeof(MainWindow).Assembly;
            var type = assembly.GetType("ZapretGui.Core.AccentManager", throwOnError: false)
                       ?? assembly.GetType("ZapretGui.Controls.AccentManager", throwOnError: false)
                       ?? assembly.GetType("ZapretGui.AccentManager", throwOnError: false);

            var method = type?.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static,
                                         binder: null, types: new[] { typeof(string) }, modifiers: null);
            if (method is null)
                return false;

            method.Invoke(null, new object?[] { preset });
            return true;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
            return false;
        }
    }

    private static (Color Start, Color Mid, Color End) PresetColors(string? preset) => preset?.ToLowerInvariant() switch
    {
        "violet" => (Rgb(0x6E, 0x5C, 0xFF), Rgb(0x8B, 0x57, 0xFF), Rgb(0xA8, 0x55, 0xF7)),
        "emerald" => (Rgb(0x35, 0xE8, 0xA6), Rgb(0x23, 0xD3, 0xA2), Rgb(0x14, 0xB8, 0xA6)),
        "rose" => (Rgb(0xFF, 0x7A, 0x9C), Rgb(0xFF, 0x54, 0x79), Rgb(0xF0, 0x35, 0x6A)),
        "amber" => (Rgb(0xFF, 0xC9, 0x4B), Rgb(0xFF, 0xA6, 0x44), Rgb(0xFF, 0x85, 0x3D)),
        _ => (Rgb(0x26, 0xE0, 0xF2), Rgb(0x29, 0xC4, 0xFA), Rgb(0x2F, 0xA8, 0xFF)),
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static T Frozen<T>(T brush) where T : Freezable
    {
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }

    // ──────────────────────────────────────────────────────── кнопки хрома

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ──────────────────────────────────────────────────────── WM_GETMINMAXINFO

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO)
            return IntPtr.Zero;

        try
        {
            // Без этого развёрнутое окно с WindowStyle=None накрывает панель задач.
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
                return IntPtr.Zero;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
            mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

            // Без ptMaxTrackSize Windows берёт размер ОСНОВНОГО монитора: на большем втором окно обрезается.
            mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
            mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

            var dpi = VisualTreeHelper.GetDpi(this);
            mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
            mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
            handled = true;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}

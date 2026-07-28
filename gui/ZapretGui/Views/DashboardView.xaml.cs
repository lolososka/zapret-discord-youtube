using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZapretGui.Controls;
using ZapretGui.Core;

namespace ZapretGui.Views;

/// <summary>
/// «Панель»: силовой диск, осциллограф пакетов, метрики процесса и службы,
/// быстрые переключатели и проверка доступа.
/// </summary>
public partial class DashboardView : System.Windows.Controls.UserControl
{
    // Порог одноколоночной раскладки с гистерезисом: без него страница
    // дребезжала бы на ширине ровно 900.
    private const double NarrowEnter = 900;
    private const double NarrowExit = 940;

    private TrafficMonitor? _traffic;
    private bool _syncing;
    private bool _narrow;   // соответствует разметке: при старте две колонки

    public DashboardView()
    {
        InitializeComponent();

        _syncing = true;
        AutoStartToggle.IsChecked = AppSettings.Current.AutoStartBypass;
        _syncing = false;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>«strategies» | «diagnostics» | «logs» | «settings».</summary>
    public event EventHandler<string>? NavigationRequested;

    // ---------- жизненный цикл ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        AutoStartToggle.IsChecked = AppSettings.Current.AutoStartBypass;
        _syncing = false;

        // Подписка снимается в Unloaded: страница переживает переключения нав-рейла.
        if (_traffic is null)
        {
            _traffic = AppState.Instance.Traffic;
            _traffic.Sampled += OnTrafficSampled;
        }

        UpdateResponsiveLayout(BodyScroll.ActualWidth, BodyScroll.ActualHeight);
        RedrawScope();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_traffic is not null)
        {
            _traffic.Sampled -= OnTrafficSampled;
            _traffic = null;
        }
    }

    // ---------- отзывчивая раскладка ----------

    private void OnBodyScrollSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout(e.NewSize.Width, e.NewSize.Height);

    private void OnAsideScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_narrow) Aside.MinHeight = Floor(e.NewSize.Height);
    }

    private void UpdateResponsiveLayout(double width, double height)
    {
        if (width < 1) return;   // первый проход раскладки: размеров ещё нет

        var narrow = _narrow ? width < NarrowExit : width < NarrowEnter;
        if (narrow != _narrow)
        {
            _narrow = narrow;
            ApplyColumnMode();
        }

        // Широкий режим: колодец обязан занять всю высоту области, иначе под графиком
        // остаётся провал. Узкий: страница просто прокручивается.
        BodyGrid.MinHeight = _narrow ? 0 : Floor(height);

        // Без потолка правая колонка отдаёт наружу всю свою высоту, внешняя полоса
        // прокрутки перехватывает работу внутренней, и колодец уезжает вверх.
        AsideScroll.MaxHeight = _narrow ? double.PositiveInfinity : Floor(height);
    }

    /// <summary>
    /// Правая колонка переезжает под колодец. Её вынимают из собственного ScrollViewer:
    /// вложенная вертикальная прокрутка перехватывает колесо мыши и страница «залипает».
    /// </summary>
    private void ApplyColumnMode()
    {
        if (_narrow)
        {
            AsideScroll.Content = null;
            AsideScroll.Visibility = Visibility.Collapsed;

            Aside.MinHeight = 0;
            Aside.Margin = new Thickness(0, 16, 0, 0);
            Grid.SetRow(Aside, 1);
            Grid.SetColumn(Aside, 0);
            Grid.SetColumnSpan(Aside, 3);
            if (!BodyGrid.Children.Contains(Aside)) BodyGrid.Children.Add(Aside);

            GapColumn.Width = new GridLength(0);
            AsideColumn.MinWidth = 0;
            AsideColumn.MaxWidth = 0;
            AsideColumn.Width = new GridLength(0);
            return;
        }

        BodyGrid.Children.Remove(Aside);
        Aside.Margin = default;
        Grid.SetRow(Aside, 0);
        Grid.SetColumn(Aside, 0);
        Grid.SetColumnSpan(Aside, 1);
        AsideScroll.Content = Aside;
        AsideScroll.Visibility = Visibility.Visible;

        AsideColumn.MaxWidth = 380;
        AsideColumn.MinWidth = 300;
        AsideColumn.Width = new GridLength(0.34, GridUnitType.Star);
        GapColumn.Width = new GridLength(24);
        Aside.MinHeight = Floor(AsideScroll.ActualHeight);
    }

    /// <summary>Округление вниз с запасом в 1 DIP: иначе subpixel-остаток рождает лишнюю полосу прокрутки.</summary>
    private static double Floor(double value) => Math.Max(0, Math.Floor(value) - 1);

    // ---------- осциллограф ----------

    private void OnTrafficSampled(object? sender, EventArgs e) => RedrawScope();

    private void OnScopeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Border.ClipToBounds в WPF остаётся прямоугольным даже при CornerRadius.
        // Отдельная геометрия обрезает сетку, заливку, линию и подпись одним радиусом.
        double radius = ScopeFrame.CornerRadius.TopLeft;
        var clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
            radius,
            radius);
        clip.Freeze();
        ScopeContent.Clip = clip;

        RedrawScope();
    }

    /// <summary>
    /// Полная перестройка кривой раз в 500 мс по событию TrafficMonitor.Sampled.
    /// 240 точек на выборку — дешевле, чем держать сдвигаемую геометрию на Rendering.
    /// </summary>
    private void RedrawScope()
    {
        double w = ScopeCanvas.ActualWidth;
        double h = ScopeCanvas.ActualHeight;
        if (w < 8 || h < 8) return;

        var traffic = _traffic ?? AppState.Instance.Traffic;
        var samples = traffic.Samples;
        int n = samples.Count;
        if (n < 2) return;

        double peak = traffic.PeakPackets;
        if (peak < 1) peak = 1;

        const double topInset = 22;              // место под микроподписью «ПАКЕТОВ / С»
        double baseline = h - 1;                 // базовая линия внизу
        double span = Math.Max(1, baseline - topInset);
        double step = w / (n - 1);

        var trace = new StreamGeometry();
        var fill = new StreamGeometry();
        double lastX = 0, lastY = baseline;

        using (var tc = trace.Open())
        using (var fc = fill.Open())
        {
            fc.BeginFigure(new Point(0, baseline), true, true);

            for (int i = 0; i < n; i++)
            {
                double x = i * step;
                double y = baseline - Math.Clamp(samples[i] / peak, 0, 1) * span;

                if (i == 0) tc.BeginFigure(new Point(x, y), false, false);
                else tc.LineTo(new Point(x, y), true, false);

                fc.LineTo(new Point(x, y), false, false);
                lastX = x;
                lastY = y;
            }

            fc.LineTo(new Point(lastX, baseline), false, false);
        }

        trace.Freeze();
        fill.Freeze();

        ScopeTrace.Data = trace;
        ScopeFill.Data = fill;

        Canvas.SetLeft(ScopeDot, Math.Min(lastX, w - 3) - 3);
        Canvas.SetTop(ScopeDot, lastY - 3);
    }

    // ---------- силовой диск ----------

    private void OnDialActivated(object sender, EventArgs e) => Toggle();

    private void OnDialDeactivated(object sender, EventArgs e) => Toggle();

    private static void Toggle()
    {
        var command = AppState.Instance.ToggleBypassCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    // ---------- навигация ----------

    private void OnStrategyChipClick(object sender, RoutedEventArgs e)
        => NavigationRequested?.Invoke(this, "strategies");

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
        => NavigationRequested?.Invoke(this, "logs");

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var owner = Window.GetWindow(this);
        var answer = MessageBox.Show(
            owner,
            "Скачать проверенный portable-релиз и установить его?\n\n" +
            "Программа сверит SHA-256, сохранит пользовательские списки и настройки, " +
            "а при неудачном запуске автоматически вернёт предыдущую версию.\n\n" +
            "Во время замены обход и служба могут ненадолго остановиться.",
            "Безопасное обновление",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        var oldContent = button.Content;
        var handoffStarted = false;
        var preparedExitApproved = false;
        button.IsEnabled = false;
        button.Content = "0%";
        AppState.Instance.Notify(
            "Скачиваем и проверяем portable-обновление…",
            ToastKind.Info);

        try
        {
            var progress = new Progress<double>(value =>
                button.Content = $"{Math.Round(value * 100):0}%");
            var prepared = await ForkUpdateService.PrepareLatestAsync(progress);
            if (!prepared.Success ||
                string.IsNullOrWhiteSpace(prepared.PlanPath) ||
                string.IsNullOrWhiteSpace(prepared.PlanSha256))
            {
                AppState.Instance.Notify(prepared.Message, ToastKind.Warning);
                MessageBox.Show(
                    owner,
                    prepared.Message + "\n\nМожно скачать релиз вручную:\n" +
                    UpdateService.DownloadUrl,
                    "Обновление не применено",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var mainWindow =
                Application.Current.MainWindow as MainWindow;
            if (mainWindow is not null &&
                !mainWindow.TryApprovePreparedExit())
            {
                AppState.Instance.Notify(
                    "Обновление отложено: сначала сохраните пользовательские списки",
                    ToastKind.Warning);
                return;
            }
            preparedExitApproved = mainWindow is not null;

            if (!PortableUpdateInstaller.LaunchHelper(
                    prepared.PlanPath,
                    prepared.PlanSha256,
                    out var launchError))
            {
                AppState.Instance.Notify(
                    "Не удалось запустить установщик обновления",
                    ToastKind.Error);
                MessageBox.Show(
                    owner,
                    "Пакет был загружен и проверен, но helper не запустился:\n\n" +
                    launchError,
                    "Обновление не применено",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            AppState.Instance.Notify(
                "Пакет проверен. Перезапускаем приложение…",
                ToastKind.Success);
            handoffStarted = true;
            button.Content = "Перезапуск…";
            if (mainWindow is not null)
                mainWindow.ExitForPreparedUpdate();
            else
                Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, fatal: false);
            AppState.Instance.Notify(
                "Не удалось подготовить обновление",
                ToastKind.Error);
            MessageBox.Show(
                owner,
                ex.Message,
                "Ошибка обновления",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (!handoffStarted)
            {
                if (preparedExitApproved &&
                    Application.Current.MainWindow is MainWindow mainWindow)
                    mainWindow.CancelPreparedExitApproval();
                button.Content = oldContent;
                button.IsEnabled = true;
            }
        }
    }

    // ---------- быстрые переключатели ----------

    private void OnGameFilterChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var state = AppState.Instance;
        if (!state.IsGameFilterOn) state.GameFilter = GameFilterMode.All;
    }

    private void OnGameFilterUnchecked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var state = AppState.Instance;
        if (state.IsGameFilterOn) state.GameFilter = GameFilterMode.Disabled;
    }

    private void OnAutoStartChecked(object sender, RoutedEventArgs e) => SaveAutoStart(true);

    private void OnAutoStartUnchecked(object sender, RoutedEventArgs e) => SaveAutoStart(false);

    private void SaveAutoStart(bool on)
    {
        if (_syncing) return;
        if (AppSettings.Current.AutoStartBypass == on) return;

        AppSettings.Current.AutoStartBypass = on;
        AppSettings.Save();
    }
}

/// <summary>BypassState → состояние силового диска.</summary>
public sealed class BypassStateToDialStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is BypassState state
            ? state switch
            {
                BypassState.Running => DialState.Running,
                BypassState.Starting or BypassState.Stopping => DialState.Arming,
                BypassState.Failed => DialState.Fault,
                _ => DialState.Stopped,
            }
            : DialState.Stopped;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

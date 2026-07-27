using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZapretGui.Core;

namespace ZapretGui.Controls;

/// <summary>
/// Карточка «Как вас видят сайты»: показывает фактический внешний адрес.
///
/// Запрос уходит только по нажатию кнопки — он раскрывает IP пользователя стороннему
/// сервису, и это должно быть его осознанным действием, а не побочным эффектом запуска.
/// </summary>
public partial class IpInfoCard : System.Windows.Controls.UserControl
{
    private const double BarWidth = 96;
    private const int BarPeriodMs = 1100;

    private static readonly Color ErrorBorderColor = Color.FromArgb(0x73, 0xFF, 0x5F, 0x6D);

    private CancellationTokenSource? _cts;
    private Storyboard? _barLoop;
    private bool _busy;

    public IpInfoCard()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelPending();
        StopBar();

        // Карточку могли выгрузить прямо во время запроса — иначе кнопка останется
        // заблокированной, когда страницу покажут снова.
        if (_busy)
        {
            _busy = false;
            CheckButton.IsEnabled = true;
            CheckButton.Content = "Проверить";
            RetryButton.IsEnabled = true;
            LoadingState.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    private void CancelPending()
    {
        var cts = _cts;
        _cts = null;

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
            // Уже отменён или освобождён — не важно.
        }

        cts.Dispose();
    }

    private async void OnCheckClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        CancelPending();

        var cts = new CancellationTokenSource();
        _cts = cts;

        EnterLoading();

        IpDetails? details = null;
        try
        {
            details = await IpInfo.LookupAsync(cts.Token);
        }
        catch
        {
            // LookupAsync не бросает, но обработчик события обязан быть безопасным.
        }

        // Пока ждали, карточку могли выгрузить или запустить новую проверку.
        if (!ReferenceEquals(_cts, cts) || cts.IsCancellationRequested)
        {
            return;
        }

        _cts = null;
        cts.Dispose();

        LeaveLoading();

        if (details is null)
        {
            ShowError();
        }
        else
        {
            ShowResult(details);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var ip = IpValue.Text;
        if (string.IsNullOrWhiteSpace(ip) || ip == "—")
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(ip);
        }
        catch
        {
            // Буфер обмена держит другой процесс — молча выходим, состояние карточки не меняется.
            return;
        }

        try
        {
            AppState.Instance.Notify("IP-адрес скопирован", ToastKind.Success);
        }
        catch
        {
            // Тост не критичен.
        }
    }

    // ==================================================================
    // Состояния
    // ==================================================================

    private void EnterLoading()
    {
        _busy = true;

        CheckButton.IsEnabled = false;
        CheckButton.Content = "Выполняется…";
        RetryButton.IsEnabled = false;

        Card.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);

        EmptyState.Visibility = Visibility.Collapsed;
        ResultState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Visible;

        StartBar();
    }

    private void LeaveLoading()
    {
        _busy = false;

        CheckButton.IsEnabled = true;
        CheckButton.Content = "Проверить снова";
        RetryButton.IsEnabled = true;
        LoadingState.Visibility = Visibility.Collapsed;

        StopBar();
    }

    private void ShowResult(IpDetails details)
    {
        Card.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);

        IpValue.Text = details.Ip;
        SetRowValue(CountryValue, ComposeCountry(details));
        SetRowValue(CityValue, details.City);
        SetRowValue(ProviderValue, details.Provider);

        StampLabel.Text = details.FromCache
            ? "ДАННЫЕ ИЗ КЭША, НЕ СТАРШЕ 5 МИНУТ"
            : "ПРОВЕРЕНО В " + DateTime.Now.ToString("HH:mm");

        ErrorState.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ResultState.Visibility = Visibility.Visible;
    }

    private void ShowError()
    {
        var brush = new SolidColorBrush(ErrorBorderColor);
        brush.Freeze();
        Card.BorderBrush = brush;

        EmptyState.Visibility = Visibility.Collapsed;
        ResultState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Visible;
    }

    /// <summary>«Россия (RU)» — код рядом с названием избавляет от догадок при экзотических странах.</summary>
    private static string? ComposeCountry(IpDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.Country))
        {
            return details.CountryCode;
        }

        if (string.IsNullOrWhiteSpace(details.CountryCode) ||
            string.Equals(details.Country, details.CountryCode, StringComparison.OrdinalIgnoreCase))
        {
            return details.Country;
        }

        return details.Country + " (" + details.CountryCode + ")";
    }

    private static void SetRowValue(System.Windows.Controls.TextBlock target, string? text)
    {
        var known = !string.IsNullOrWhiteSpace(text);

        target.Text = known ? text! : "нет данных";
        target.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            known ? "BrushTextPrimary" : "BrushTextTertiary");
    }

    // ==================================================================
    // Индикатор неопределённой длительности (§7 Indeterminate, §9)
    // ==================================================================

    private void StartBar()
    {
        ProgressChip.Fill = BuildBarBrush();
        ProgressTrack.Visibility = Visibility.Visible;

        var track = ProgressTrack.ActualWidth;

        // ReducedMotion или ещё не размеренный трек: статичная заливка на 30 % вместо бегунка.
        if (Fx.ReducedMotion || track <= 0)
        {
            StopBar(keepVisible: true);
            ProgressChip.Width = double.NaN;
            ProgressChip.HorizontalAlignment = HorizontalAlignment.Stretch;
            ProgressChip.Opacity = 0.3;
            ProgressShift.X = 0;
            return;
        }

        ProgressChip.Width = BarWidth;
        ProgressChip.HorizontalAlignment = HorizontalAlignment.Left;
        ProgressChip.Opacity = 1;

        var animation = new DoubleAnimation(-BarWidth, track, new Duration(TimeSpan.FromMilliseconds(BarPeriodMs)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(animation, ProgressShift);
        Storyboard.SetTargetProperty(animation, new PropertyPath(TranslateTransform.XProperty));

        _barLoop = new Storyboard();
        _barLoop.Children.Add(animation);
        _barLoop.Begin(this, isControllable: true);
    }

    private void StopBar(bool keepVisible = false)
    {
        if (_barLoop is not null)
        {
            _barLoop.Stop(this);
            _barLoop.Remove(this);
            _barLoop = null;
        }

        ProgressShift.X = -BarWidth;

        if (!keepVisible)
        {
            ProgressTrack.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Кисть бегунка строится в коде: концы должны быть акцентом с нулевой альфой,
    /// а вывести такой цвет из кисти-ресурса в XAML нельзя.
    /// </summary>
    private Brush BuildBarBrush()
    {
        var accent = Color.FromRgb(0x29, 0xC4, 0xFA);
        if (TryFindResource("ColorAccentMid") is Color fromTheme)
        {
            accent = fromTheme;
        }

        var transparent = Color.FromArgb(0x00, accent.R, accent.G, accent.B);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };

        brush.GradientStops.Add(new GradientStop(transparent, 0.0));
        brush.GradientStops.Add(new GradientStop(accent, 0.5));
        brush.GradientStops.Add(new GradientStop(transparent, 1.0));
        brush.Freeze();

        return brush;
    }
}

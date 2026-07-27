using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace ZapretGui.Controls;

/// <summary>
/// Амбиентная подсветка окна: слои 2 и 3 из §5. Два эллипса под BlurEffect в BitmapCache
/// и дизер-плитка поверх. Дрейф — только Transform и Opacity, чтобы кэш не пересобирался.
/// </summary>
public partial class AmbientBackground : System.Windows.Controls.UserControl
{
    private const double OpacityIdle = 0.40;
    private const double OpacityLive = 0.55;
    private const double OpacityDegraded = 0.35;
    private const int FadeMs = 400;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(AmbientBackground),
            new PropertyMetadata(false, OnIsActiveChanged));

    // Приватная привязка к DynamicResource акцента: колбэк срабатывает при смене пресета.
    private static readonly DependencyProperty AccentSourceProperty =
        DependencyProperty.Register(
            "AccentSource", typeof(Brush), typeof(AmbientBackground),
            new PropertyMetadata(null, OnAccentSourceChanged));

    private readonly Effect? _blurA;
    private readonly Effect? _blurB;

    private readonly TranslateTransform _moveA = new(-60, 40);
    private readonly ScaleTransform _scaleA = new(1.00, 1.00);
    private readonly TranslateTransform _moveB = new(40, -30);
    private readonly ScaleTransform _scaleB = new(1.06, 1.06);

    private Storyboard? _driftA;
    private Storyboard? _driftB;

    private bool _ready;
    private bool _degraded;
    private bool _driftRunning;
    private bool _paused;

    public AmbientBackground()
    {
        InitializeComponent();

        _blurA = GlowA.Effect;
        _blurB = GlowB.Effect;

        var groupA = new TransformGroup();
        groupA.Children.Add(_scaleA);
        groupA.Children.Add(_moveA);
        GlowA.RenderTransform = groupA;

        var groupB = new TransformGroup();
        groupB.Children.Add(_scaleB);
        groupB.Children.Add(_moveB);
        GlowB.RenderTransform = groupB;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        SetResourceReference(AccentSourceProperty, "BrushAccentMid");
        _ready = true;
    }

    /// <summary>Обход включён: подсветка чуть ярче (0.40 ↔ 0.55).</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private Brush? AccentSource => (Brush?)GetValue(AccentSourceProperty);

    /// <summary>Окно свернули или потеряло фокус — дрейф встаёт, CPU уходит в ноль.</summary>
    public void Pause()
    {
        if (!_driftRunning || _paused)
        {
            return;
        }

        _paused = true;
        _driftA?.Pause(this);
        _driftB?.Pause(this);
    }

    /// <summary>Окно снова активно. Заодно перечитывает режим движения.</summary>
    public void Resume()
    {
        ApplyMotionMode();

        if (!_driftRunning || !_paused)
        {
            return;
        }

        _paused = false;
        _driftA?.Resume(this);
        _driftB?.Resume(this);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (AmbientBackground)d;
        if (!self._ready)
        {
            return;
        }

        self.SetHostOpacity(self.TargetOpacity(), animate: true);
    }

    private static void OnAccentSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AmbientBackground)d).ApplyAccent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAccent();
        ApplyMotionMode();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopDrift();

    private double TargetOpacity() => _degraded
        ? OpacityDegraded
        : IsActive ? OpacityLive : OpacityIdle;

    private void ApplyAccent()
    {
        if (AccentSource is not SolidColorBrush accent)
        {
            return;
        }

        if (GlowA.Fill is not RadialGradientBrush brush || brush.GradientStops.Count < 2)
        {
            return;
        }

        // Кисть из BAML может оказаться замороженной — тогда работаем по копии.
        if (brush.IsFrozen)
        {
            brush = brush.Clone();
            GlowA.Fill = brush;
        }

        Color c = accent.Color;
        brush.GradientStops[0].Color = Color.FromArgb(0x24, c.R, c.G, c.B);
        brush.GradientStops[1].Color = Color.FromArgb(0x00, c.R, c.G, c.B);
    }

    private void ApplyMotionMode()
    {
        // §7, последняя строка: ReducedMotion или Tier < 2 — без блюра, без дрейфа, 0.35.
        bool degraded = Fx.ReducedMotion || (RenderCapability.Tier >> 16) < 2;
        bool changed = degraded != _degraded;
        _degraded = degraded;

        if (degraded)
        {
            StopDrift();
            GlowA.Effect = null;
            GlowB.Effect = null;
            SetHostOpacity(OpacityDegraded, animate: false);
            return;
        }

        if (changed || GlowA.Effect is null)
        {
            GlowA.Effect = _blurA;
            GlowB.Effect = _blurB;
        }

        StartDrift();
        SetHostOpacity(TargetOpacity(), animate: false);
    }

    private void StartDrift()
    {
        if (_driftRunning)
        {
            return;
        }

        // Период 46 с / 61 с, AutoReverse + Forever, SineEase EaseInOut (§7 AmbientDrift).
        _driftA ??= BuildDrift(_moveA, _scaleA, -60, 70, 40, -30, 1.00, 1.12, 46000);
        _driftB ??= BuildDrift(_moveB, _scaleB, 40, -50, -30, 50, 1.06, 0.94, 61000);

        _driftA.Begin(this, isControllable: true);
        _driftB.Begin(this, isControllable: true);
        _driftRunning = true;
        _paused = false;
    }

    private void StopDrift()
    {
        if (!_driftRunning)
        {
            return;
        }

        _driftA?.Stop(this);
        _driftB?.Stop(this);
        _driftA?.Remove(this);
        _driftB?.Remove(this);
        _driftRunning = false;
        _paused = false;
    }

    private static Storyboard BuildDrift(
        TranslateTransform move, ScaleTransform scale,
        double x0, double x1, double y0, double y1,
        double s0, double s1, int periodMs)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(periodMs));

        var board = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
        };

        void Add(DependencyObject target, DependencyProperty property, double from, double to)
        {
            var animation = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            board.Children.Add(animation);
        }

        Add(move, TranslateTransform.XProperty, x0, x1);
        Add(move, TranslateTransform.YProperty, y0, y1);
        Add(scale, ScaleTransform.ScaleXProperty, s0, s1);
        Add(scale, ScaleTransform.ScaleYProperty, s0, s1);

        return board;
    }

    private void SetHostOpacity(double to, bool animate)
    {
        AmbientHost.BeginAnimation(OpacityProperty, null);

        if (!animate || _degraded)
        {
            AmbientHost.Opacity = to;
            return;
        }

        var animation = new DoubleAnimation(AmbientHost.Opacity, to, new Duration(TimeSpan.FromMilliseconds(FadeMs)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        animation.Completed += (_, _) =>
        {
            AmbientHost.BeginAnimation(OpacityProperty, null);
            AmbientHost.Opacity = to;
        };

        AmbientHost.BeginAnimation(OpacityProperty, animation);
    }
}

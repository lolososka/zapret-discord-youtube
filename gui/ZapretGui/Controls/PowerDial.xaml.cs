using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZapretGui.Core;

namespace ZapretGui.Controls;

public enum DialState
{
    Stopped,
    Arming,
    Running,
    Fault,
}

/// <summary>
/// Силовой диск (§6). Владелец задаёт <see cref="State"/>; контрол сам проигрывает
/// цепочку «нажатие → взвод → зажигание → рабочий цикл» и её обратную сторону.
/// </summary>
public partial class PowerDial : System.Windows.Controls.UserControl
{
    private const double PoolRunningOpacity = 0.46;
    private const double MaskArmedRadius = 0.62;

    private static readonly Color ColorGlyphStopped = Color.FromRgb(0x6E, 0x7A, 0x85);
    private static readonly Color ColorGlyphDanger = Color.FromRgb(0xFF, 0x5F, 0x6D);
    private static readonly Color ColorGlyphDisabled = Color.FromRgb(0x3E, 0x47, 0x53);
    private static readonly Color ColorFaceIdle = Color.FromRgb(0x14, 0x19, 0x22);
    private static readonly Color ColorFacePressed = Color.FromRgb(0x0E, 0x12, 0x18);
    private static readonly Color ColorFaceRunningBase = Color.FromRgb(0x10, 0x18, 0x20);
    private static readonly Color ColorTickIdle = Color.FromRgb(0x26, 0x2D, 0x37);

    private static readonly Color FallbackAccentStart = Color.FromRgb(0x26, 0xE0, 0xF2);
    private static readonly Color FallbackAccentMid = Color.FromRgb(0x29, 0xC4, 0xFA);
    private static readonly Color FallbackAccentEnd = Color.FromRgb(0x2F, 0xA8, 0xFF);

    private static readonly IEasingFunction EaseCircleOut = new CircleEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EasePower3Out = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3 };
    private static readonly IEasingFunction EaseQuadOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseQuadIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction EaseCubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseQuintIn = new QuinticEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction EaseQuartOut = new QuarticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseSineInOut = new SineEase { EasingMode = EasingMode.EaseInOut };
    private static readonly IEasingFunction EaseBackOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    // Одна замороженная DrawingBrush на цвет — 60 рисок остаются одним визуалом.
    private static readonly Dictionary<uint, DrawingBrush> TickBrushCache = new();

    private readonly List<TextBlock> _uptimeGlyphs = new();

    private Storyboard? _armSb;
    private Storyboard? _igniteSb;
    private Storyboard? _offSb;
    private Storyboard? _faultSb;
    private Storyboard? _captionSb;
    private Storyboard? _odometerSb;
    private Storyboard? _pressSb;
    private Storyboard? _orbitSb;
    private Storyboard? _breatheSb;
    private Storyboard? _pingSb;

    private Color _faceRestColor = ColorFaceIdle;
    private bool _ready;
    private bool _pressed;
    private int _gen;

    public PowerDial()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;

        HitTarget.Click += OnHitClick;
        HitTarget.PreviewMouseLeftButtonDown += OnHitPressStart;
        HitTarget.PreviewMouseLeftButtonUp += OnHitPressEnd;
        HitTarget.MouseLeave += OnHitMouseLeave;
        HitTarget.PreviewKeyDown += OnHitKeyDown;
    }

    // ==================== Свойства зависимостей ====================

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(DialState), typeof(PowerDial),
        new FrameworkPropertyMetadata(DialState.Stopped, OnStatePropertyChanged));

    public DialState State
    {
        get => (DialState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public static readonly DependencyProperty UptimeTextProperty = DependencyProperty.Register(
        nameof(UptimeText), typeof(string), typeof(PowerDial),
        new FrameworkPropertyMetadata("--:--:--", OnUptimeTextPropertyChanged));

    public string UptimeText
    {
        get => (string)GetValue(UptimeTextProperty);
        set => SetValue(UptimeTextProperty, value);
    }

    /// <summary>false — не найден bin\winws.exe: диск заблокирован и обесцвечен.</summary>
    public static readonly DependencyProperty IsEngineAvailableProperty = DependencyProperty.Register(
        nameof(IsEngineAvailable), typeof(bool), typeof(PowerDial),
        new FrameworkPropertyMetadata(true, OnEngineAvailablePropertyChanged));

    public bool IsEngineAvailable
    {
        get => (bool)GetValue(IsEngineAvailableProperty);
        set => SetValue(IsEngineAvailableProperty, value);
    }

    /// <summary>Клик в состоянии Stopped или Fault.</summary>
    public event EventHandler? Activated;

    /// <summary>Клик в состоянии Running.</summary>
    public event EventHandler? Deactivated;

    // ==================== Жизненный цикл ====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            IdleTicks.Fill = TickBrush(ColorTickIdle);
            BuildUptimeGlyphs(UptimeText ?? "--:--:--");
            _ready = true;
        }

        ApplyAccent();
        ApplyStatic(State);

        if (State == DialState.Running) StartRunLoops();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopEverything();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Невидимый диск не должен крутить бесконечные раскадровки.
        if ((bool)e.NewValue)
        {
            if (_ready && State == DialState.Running && _orbitSb is null)
            {
                StartRunLoops();
                return;
            }
            TryControl(_orbitSb, resume: true);
            TryControl(_breatheSb, resume: true);
            TryControl(_pingSb, resume: true);
        }
        else
        {
            TryControl(_orbitSb, resume: false);
            TryControl(_breatheSb, resume: false);
            TryControl(_pingSb, resume: false);
        }
    }

    private void TryControl(Storyboard? sb, bool resume)
    {
        if (sb is null) return;
        try
        {
            if (resume) sb.Resume(this);
            else sb.Pause(this);
        }
        catch (InvalidOperationException) { /* раскадровка уже снята */ }
    }

    // ==================== Реакция на смену свойств ====================

    private static void OnStatePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PowerDial)d).HandleStateChange((DialState)e.NewValue);

    private void HandleStateChange(DialState to)
    {
        if (!_ready)
        {
            UpdateInteractivity();
            return;
        }

        StopTransient();
        UpdateInteractivity();

        switch (to)
        {
            case DialState.Arming: BeginArm(); break;
            case DialState.Running: BeginRunning(); break;
            case DialState.Fault: BeginFault(); break;
            default: BeginStopped(); break;
        }
    }

    private static void OnUptimeTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PowerDial)d).ApplyUptime(e.NewValue as string);

    private static void OnEngineAvailablePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dial = (PowerDial)d;
        dial.UpdateInteractivity();
        if (dial._ready) dial.ApplyStatic(dial.State);
    }

    private void UpdateInteractivity()
    {
        HitTarget.IsEnabled = IsEngineAvailable && State != DialState.Arming;
        HitTarget.ToolTip = IsEngineAvailable ? null : "Не найден bin\\winws.exe";
    }

    // ==================== Ввод ====================

    private bool CanToggle => IsEngineAvailable && State != DialState.Arming;

    private void OnHitClick(object sender, RoutedEventArgs e)
    {
        // Состояние диска ведёт владелец, поэтому собственное переключение ToggleButton откатываем.
        HitTarget.IsChecked = State == DialState.Running;
        if (!CanToggle) return;

        if (State == DialState.Running) Deactivated?.Invoke(this, EventArgs.Empty);
        else Activated?.Invoke(this, EventArgs.Empty);
    }

    private void OnHitKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;   // Space ToggleButton обрабатывает сам
        e.Handled = true;
        OnHitClick(sender, new RoutedEventArgs());
    }

    private void OnHitPressStart(object sender, MouseButtonEventArgs e)
    {
        if (!CanToggle) return;
        _pressed = true;

        var sb = NewStoryboard();
        Track(sb, Anim(DialScale.ScaleX, 0.968, 70, EaseQuadOut), DialScale, ScaleTransform.ScaleXProperty);
        Track(sb, Anim(DialScale.ScaleY, 0.968, 70, EaseQuadOut), DialScale, ScaleTransform.ScaleYProperty);
        Track(sb, Anim(FaceStopCore.Color, ColorFacePressed, 70, EaseQuadOut), FaceStopCore, GradientStop.ColorProperty);
        sb.Completed += (_, _) =>
        {
            if (!_pressed) return;
            DialScale.ScaleX = DialScale.ScaleY = 0.968;
            FaceStopCore.Color = ColorFacePressed;
        };

        RestartPress(sb);
    }

    private void OnHitPressEnd(object sender, MouseButtonEventArgs e) => EndPress();

    private void OnHitMouseLeave(object sender, MouseEventArgs e) => EndPress();

    private void EndPress()
    {
        if (!_pressed) return;
        _pressed = false;

        // Единственный овершут во всём приложении — и только если движение не урезано.
        IEasingFunction ease = ReducedMotion ? EaseCubicOut : EaseBackOut;

        var sb = NewStoryboard();
        Track(sb, Anim(DialScale.ScaleX, 1.0, 160, ease), DialScale, ScaleTransform.ScaleXProperty);
        Track(sb, Anim(DialScale.ScaleY, 1.0, 160, ease), DialScale, ScaleTransform.ScaleYProperty);
        Track(sb, Anim(FaceStopCore.Color, _faceRestColor, 160, EaseQuadOut), FaceStopCore, GradientStop.ColorProperty);
        sb.Completed += (_, _) =>
        {
            DialScale.ScaleX = DialScale.ScaleY = 1.0;
            if (State is DialState.Stopped or DialState.Fault) FaceStopCore.Color = _faceRestColor;
        };

        RestartPress(sb);
    }

    private void RestartPress(Storyboard sb)
    {
        StopSb(ref _pressSb);
        _pressSb = sb;
        sb.Begin(this, true);
    }

    // ==================== Состояния и цепочки ====================

    /// <summary>Мгновенная выкладка состояния — без анимации (первичный показ, смена доступности движка).</summary>
    private void ApplyStatic(DialState state)
    {
        Color accent = AccentMid();
        bool armed = state is DialState.Running or DialState.Fault;

        SweepArc.Angle = armed ? 360 : 0;
        TickMask.RadiusX = TickMask.RadiusY = armed ? MaskArmedRadius : 0;

        AmbientPool.Opacity = state == DialState.Running ? PoolRunningOpacity : 0;
        PoolScale.ScaleX = PoolScale.ScaleY = 1.0;

        ScannerArc.Opacity = state == DialState.Running ? 1 : 0;

        ShockRing.Opacity = 0;
        ShockScale.ScaleX = ShockScale.ScaleY = 0.62;

        DialScale.ScaleX = DialScale.ScaleY = 1.0;
        DialShake.X = 0;

        _faceRestColor = state == DialState.Running
            ? Over(accent, 0x1F, ColorFaceRunningBase)
            : ColorFaceIdle;
        FaceStopCore.Color = _faceRestColor;

        GlyphBrush.Color = GlyphColorFor(state, accent);
        Glyph.StrokeThickness = state == DialState.Running ? 2.5 : 2.25;

        if (state == DialState.Fault)
        {
            SweepStopStart.Color = ColorGlyphDanger;
            SweepStopMid.Color = ColorGlyphDanger;
            SweepStopEnd.Color = ColorGlyphDanger;
        }

        SetCaption(state);
        CaptionText.Opacity = 1;

        HitTarget.IsChecked = state == DialState.Running;
        CommitUptimeGlyphs();
    }

    private Color GlyphColorFor(DialState state, Color accent)
    {
        if (!IsEngineAvailable) return ColorGlyphDisabled;
        return state switch
        {
            DialState.Running => accent,
            DialState.Fault => ColorGlyphDanger,
            _ => ColorGlyphStopped,
        };
    }

    /// <summary>ArmSweep: 780 мс — дуга обгоняет фронт маски, риски «зажигаются» ведущей кромкой.</summary>
    private void BeginArm()
    {
        int gen = _gen;
        ApplyAccent();

        SweepArc.Angle = 0;
        TickMask.RadiusX = TickMask.RadiusY = 0;
        ShockRing.Opacity = 0;
        ScannerArc.Opacity = 0;
        AmbientPool.Opacity = 0;

        if (ReducedMotion)
        {
            SweepArc.Angle = 360;
            TickMask.RadiusX = TickMask.RadiusY = MaskArmedRadius;
            SetCaption(DialState.Arming);
            CaptionText.Opacity = 1;
            return;
        }

        var sb = NewStoryboard();
        Track(sb, Anim(0.0, 360.0, 780, EaseCircleOut), SweepArc, ArcSegmentShape.AngleProperty);
        Track(sb, Anim(0.0, MaskArmedRadius, 780, EasePower3Out), TickMask, RadialGradientBrush.RadiusXProperty);
        Track(sb, Anim(0.0, MaskArmedRadius, 780, EasePower3Out), TickMask, RadialGradientBrush.RadiusYProperty);
        sb.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            SweepArc.Angle = 360;
            TickMask.RadiusX = TickMask.RadiusY = MaskArmedRadius;
        };

        _armSb = sb;
        sb.Begin(this, true);

        CrossFadeCaption(DialState.Arming, 390);
    }

    /// <summary>Ignite + LockPulse + GlyphIgnite + OdometerFlip, затем рабочие циклы.</summary>
    private void BeginRunning()
    {
        int gen = _gen;
        ApplyAccent();

        Color accent = AccentMid();
        Color faceRunning = Over(accent, 0x1F, ColorFaceRunningBase);
        _faceRestColor = faceRunning;

        // Взвода могло не быть (восстановление состояния при старте приложения) — доводим мгновенно.
        SweepArc.Angle = 360;
        TickMask.RadiusX = TickMask.RadiusY = MaskArmedRadius;
        HitTarget.IsChecked = true;

        if (ReducedMotion)
        {
            GlyphBrush.Color = accent;
            Glyph.StrokeThickness = 2.5;
            FaceStopCore.Color = faceRunning;
            AmbientPool.Opacity = PoolRunningOpacity;
            ScannerArc.Opacity = 1;
            ShockRing.Opacity = 0;
            SetCaption(DialState.Running);
            CaptionText.Opacity = 1;
            CommitUptimeGlyphs();
            return;
        }

        var sb = NewStoryboard();

        // Ignite — 620 мс.
        Track(sb, Anim(0.62, 1.55, 620, EasePower3Out), ShockScale, ScaleTransform.ScaleXProperty);
        Track(sb, Anim(0.62, 1.55, 620, EasePower3Out), ShockScale, ScaleTransform.ScaleYProperty);
        Track(sb, Anim(0.85, 0.0, 620, EaseQuadIn), ShockRing, UIElement.OpacityProperty);
        Track(sb, Anim(0.0, 1.0, 620, EaseQuadIn), ScannerArc, UIElement.OpacityProperty);

        // LockPulse — 340 мс, перелом на 40 %.
        Track(sb, Keys(0.0, (136, 0.72), (340, PoolRunningOpacity)), AmbientPool, UIElement.OpacityProperty);
        Track(sb, Keys(0.94, (136, 1.04), (340, 1.0)), PoolScale, ScaleTransform.ScaleXProperty);
        Track(sb, Keys(0.94, (136, 1.04), (340, 1.0)), PoolScale, ScaleTransform.ScaleYProperty);

        // GlyphIgnite — 200 мс.
        Track(sb, Anim(ColorGlyphStopped, accent, 200, null), GlyphBrush, SolidColorBrush.ColorProperty);
        Track(sb, Anim(2.25, 2.50, 200, EaseQuadOut), Glyph, System.Windows.Shapes.Shape.StrokeThicknessProperty);
        Track(sb, Anim(ColorFaceIdle, faceRunning, 200, null), FaceStopCore, GradientStop.ColorProperty);

        sb.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            ShockRing.Opacity = 0;
            ShockScale.ScaleX = ShockScale.ScaleY = 0.62;
            ScannerArc.Opacity = 1;
            AmbientPool.Opacity = PoolRunningOpacity;
            PoolScale.ScaleX = PoolScale.ScaleY = 1.0;
            GlyphBrush.Color = accent;
            Glyph.StrokeThickness = 2.5;
            FaceStopCore.Color = faceRunning;
            StartRunLoops();
        };

        _igniteSb = sb;
        sb.Begin(this, true);

        CrossFadeCaption(DialState.Running, 90);
        PlayOdometerFlip();
    }

    /// <summary>PowerOff / DisarmCollapse — 260 мс.</summary>
    private void BeginStopped()
    {
        int gen = _gen;
        StopBreathe();

        // Возврат из Fault: дуга снова акцентная (State уже Stopped, guard в ApplyAccent пропускает).
        ApplyAccent();
        _faceRestColor = ColorFaceIdle;

        if (ReducedMotion)
        {
            StopRunLoops();
            ApplyStatic(DialState.Stopped);
            return;
        }

        var sb = NewStoryboard();
        Track(sb, Anim(SweepArc.Angle, 0.0, 260, EaseQuintIn), SweepArc, ArcSegmentShape.AngleProperty);
        Track(sb, Anim(TickMask.RadiusX, 0.0, 260, EaseQuintIn), TickMask, RadialGradientBrush.RadiusXProperty);
        Track(sb, Anim(TickMask.RadiusY, 0.0, 260, EaseQuintIn), TickMask, RadialGradientBrush.RadiusYProperty);
        Track(sb, Anim(AmbientPool.Opacity, 0.0, 220, EaseQuintIn), AmbientPool, UIElement.OpacityProperty);
        Track(sb, Anim(ScannerArc.Opacity, 0.0, 180, EaseQuintIn), ScannerArc, UIElement.OpacityProperty);
        Track(sb, Anim(GlyphBrush.Color, GlyphColorFor(DialState.Stopped, AccentMid()), 140, null), GlyphBrush, SolidColorBrush.ColorProperty);
        Track(sb, Anim(2.50, 2.25, 140, EaseQuadOut), Glyph, System.Windows.Shapes.Shape.StrokeThicknessProperty);
        Track(sb, Anim(FaceStopCore.Color, ColorFaceIdle, 140, null), FaceStopCore, GradientStop.ColorProperty);

        sb.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            StopRunLoops();                       // орбита гасится ровно на 260 мс
            ApplyStatic(DialState.Stopped);
        };

        _offSb = sb;
        sb.Begin(this, true);

        CrossFadeCaption(DialState.Stopped, 90);
    }

    /// <summary>Fault — 420 мс тряски и перекраска дуги с глифом в BrushDanger.</summary>
    private void BeginFault()
    {
        int gen = _gen;
        StopRunLoops();

        AmbientPool.Opacity = 0;
        ScannerArc.Opacity = 0;
        _faceRestColor = ColorFaceIdle;

        if (ReducedMotion)
        {
            ApplyStatic(DialState.Fault);
            return;
        }

        var sb = NewStoryboard();

        var shake = new DoubleAnimationUsingKeyFrames
        {
            Duration = Ms(420),
            FillBehavior = FillBehavior.Stop,
        };
        shake.KeyFrames.Add(Shake(0, 0));
        shake.KeyFrames.Add(Shake(70, -7));
        shake.KeyFrames.Add(Shake(150, 6));
        shake.KeyFrames.Add(Shake(230, -4));
        shake.KeyFrames.Add(Shake(310, 2));
        shake.KeyFrames.Add(Shake(420, 0));
        Track(sb, shake, DialShake, TranslateTransform.XProperty);

        Track(sb, Anim(SweepStopStart.Color, ColorGlyphDanger, 140, null), SweepStopStart, GradientStop.ColorProperty);
        Track(sb, Anim(SweepStopMid.Color, ColorGlyphDanger, 140, null), SweepStopMid, GradientStop.ColorProperty);
        Track(sb, Anim(SweepStopEnd.Color, ColorGlyphDanger, 140, null), SweepStopEnd, GradientStop.ColorProperty);
        Track(sb, Anim(GlyphBrush.Color, GlyphColorFor(DialState.Fault, AccentMid()), 140, null), GlyphBrush, SolidColorBrush.ColorProperty);
        Track(sb, Anim(FaceStopCore.Color, ColorFaceIdle, 140, null), FaceStopCore, GradientStop.ColorProperty);

        sb.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            DialShake.X = 0;
            ApplyStatic(DialState.Fault);
        };

        _faultSb = sb;
        sb.Begin(this, true);

        CrossFadeCaption(DialState.Fault, 90);
    }

    private static EasingDoubleKeyFrame Shake(double atMs, double value)
        => new(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs)), EaseCubicOut);

    // ==================== Бесконечные циклы ====================

    private void StartRunLoops()
    {
        StopRunLoops();
        if (ReducedMotion || !IsVisible) return;

        var orbit = new Storyboard();
        var spin = new DoubleAnimation(0, 360, Ms(2600)) { RepeatBehavior = RepeatBehavior.Forever };
        Track(orbit, spin, ScannerRotate, RotateTransform.AngleProperty);
        _orbitSb = orbit;
        orbit.Begin(this, true);

        var breathe = new Storyboard();
        var pulse = new DoubleAnimation(0.40, 0.52, Ms(2400))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
            EasingFunction = EaseSineInOut,
        };
        Track(breathe, pulse, AmbientPool, UIElement.OpacityProperty);
        _breatheSb = breathe;
        breathe.Begin(this, true);

        // Радарный импульс: кольцо расходится наружу и гаснет раз в 2.6 с.
        var ping = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        var grow = new DoubleAnimationUsingKeyFrames();
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.26, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400)))
        {
            EasingFunction = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut },
        });
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(1.26, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        grow.Duration = Ms(2600);

        var growY = grow.Clone();

        var fade = new DoubleAnimationUsingKeyFrames { Duration = Ms(2600) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.00, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.00, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.00, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));

        Track(ping, grow, PingScale, ScaleTransform.ScaleXProperty);
        Track(ping, growY, PingScale, ScaleTransform.ScaleYProperty);
        Track(ping, fade, PingRing, UIElement.OpacityProperty);
        _pingSb = ping;
        ping.Begin(this, true);
    }

    private void StopBreathe()
    {
        StopSb(ref _breatheSb);
        if (State == DialState.Running) AmbientPool.Opacity = PoolRunningOpacity;
    }

    private void StopRunLoops()
    {
        StopSb(ref _orbitSb);
        StopSb(ref _breatheSb);
        StopSb(ref _pingSb);
        PingRing.Opacity = 0;
    }

    // ==================== Подпись состояния ====================

    private void SetCaption(DialState state)
    {
        string text;
        string brushKey;

        if (!IsEngineAvailable && state != DialState.Running)
        {
            text = "НЕТ WINWS.EXE";
            brushKey = "BrushTextDisabled";
        }
        else
        {
            switch (state)
            {
                case DialState.Arming:
                    text = "ЗАПУСК…";
                    brushKey = "BrushStateArming";
                    break;
                case DialState.Running:
                    text = "ОБХОД ВКЛЮЧЁН";
                    brushKey = "BrushStateRunning";
                    break;
                case DialState.Fault:
                    text = "НЕ ЗАПУСТИЛОСЬ";
                    brushKey = "BrushDanger";
                    break;
                default:
                    text = "ОБХОД ВЫКЛЮЧЕН";
                    brushKey = "BrushTextSecondary";
                    break;
            }
        }

        CaptionText.Text = text;
        // Акцентная кисть обязана оставаться динамической — её подменяет переключатель акцента.
        CaptionText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private void CrossFadeCaption(DialState state, double halfMs)
    {
        int gen = _gen;

        if (ReducedMotion)
        {
            SetCaption(state);
            CaptionText.Opacity = 1;
            return;
        }

        var outSb = NewStoryboard();
        Track(outSb, Anim(CaptionText.Opacity, 0.0, halfMs, EaseQuadOut), CaptionText, UIElement.OpacityProperty);
        outSb.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            CaptionText.Opacity = 0;
            SetCaption(state);

            var inSb = NewStoryboard();
            Track(inSb, Anim(0.0, 1.0, halfMs, EaseQuadOut), CaptionText, UIElement.OpacityProperty);
            inSb.Completed += (_, _) =>
            {
                if (gen != _gen) return;
                CaptionText.Opacity = 1;
            };
            _captionSb = inSb;
            inSb.Begin(this, true);
        };

        StopSb(ref _captionSb);
        _captionSb = outSb;
        outSb.Begin(this, true);
    }

    // ==================== Наработка ====================

    private void ApplyUptime(string? text)
    {
        string value = string.IsNullOrEmpty(text) ? "--:--:--" : text;
        if (!_ready) return;

        if (_uptimeGlyphs.Count != value.Length)
        {
            BuildUptimeGlyphs(value);
            CommitUptimeGlyphs();
            return;
        }

        for (int i = 0; i < value.Length; i++)
            _uptimeGlyphs[i].Text = value[i].ToString();
    }

    private void BuildUptimeGlyphs(string value)
    {
        UptimeHost.Children.Clear();
        _uptimeGlyphs.Clear();

        var style = TryFindResource("HeroNumericStyle") as Style;
        foreach (char ch in value)
        {
            var block = new TextBlock
            {
                Text = ch.ToString(),
                Style = style,
                RenderTransform = new TranslateTransform(),
            };
            _uptimeGlyphs.Add(block);
            UptimeHost.Children.Add(block);
        }
    }

    private void CommitUptimeGlyphs()
    {
        foreach (TextBlock block in _uptimeGlyphs)
        {
            block.Opacity = 1;
            if (block.RenderTransform is TranslateTransform t) t.Y = 0;
        }
    }

    /// <summary>OdometerFlip: 8 разрядов со сдвигом 30 мс.</summary>
    private void PlayOdometerFlip()
    {
        if (ReducedMotion || _uptimeGlyphs.Count == 0)
        {
            CommitUptimeGlyphs();
            return;
        }

        int gen = _gen;
        var sb = NewStoryboard();

        for (int i = 0; i < _uptimeGlyphs.Count; i++)
        {
            TextBlock block = _uptimeGlyphs[i];
            if (block.RenderTransform is not TranslateTransform shift) continue;

            Track(sb, Anim(10.0, 0.0, 120, EaseQuartOut, i * 30), shift, TranslateTransform.YProperty);
            Track(sb, Anim(0.0, 1.0, 120, EaseQuartOut, i * 30), block, UIElement.OpacityProperty);
        }

        sb.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            CommitUptimeGlyphs();
        };

        StopSb(ref _odometerSb);
        _odometerSb = sb;
        sb.Begin(this, true);
    }

    // ==================== Акцент ====================

    private Color AccentMid() => ResourceColor("ColorAccentMid", FallbackAccentMid);

    private Color ResourceColor(string key, Color fallback)
        => TryFindResource(key) is Color color ? color : fallback;

    private void ApplyAccent()
    {
        Color start = ResourceColor("ColorAccentStart", FallbackAccentStart);
        Color mid = ResourceColor("ColorAccentMid", FallbackAccentMid);
        Color end = ResourceColor("ColorAccentEnd", FallbackAccentEnd);

        PoolStopCore.Color = WithAlpha(mid, 0x59);
        PoolStopMid.Color = WithAlpha(mid, 0x1F);
        PoolStopEdge.Color = WithAlpha(mid, 0x00);

        ScannerStopTail.Color = WithAlpha(mid, 0x00);
        ScannerStopHead.Color = WithAlpha(mid, 0xB3);

        LiveTicks.Fill = TickBrush(WithAlpha(mid, 0x52));

        if (State == DialState.Fault) return;

        SweepStopStart.Color = start;
        SweepStopMid.Color = mid;
        SweepStopEnd.Color = end;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Over(Color foreground, byte alpha, Color background)
    {
        double a = alpha / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * a + background.R * (1 - a)),
            (byte)Math.Round(foreground.G * a + background.G * (1 - a)),
            (byte)Math.Round(foreground.B * a + background.B * (1 - a)));
    }

    // ==================== Кольцо рисок ====================

    private static DrawingBrush TickBrush(Color color)
    {
        uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | (uint)color.B;
        if (TickBrushCache.TryGetValue(key, out DrawingBrush? cached)) return cached;

        var ticks = new GeometryGroup();
        for (int i = 0; i < 60; i++)
        {
            var tick = new RectangleGeometry(new Rect(119.5, 5, 1, 6))
            {
                Transform = new RotateTransform(i * 6.0, 120, 120),
            };
            ticks.Children.Add(tick);
        }
        ticks.Freeze();

        var drawing = new GeometryDrawing(new SolidColorBrush(color), (Pen?)null, ticks);
        drawing.Freeze();

        var brush = new DrawingBrush(drawing)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 240, 240),
        };
        brush.Freeze();

        TickBrushCache[key] = brush;
        return brush;
    }

    // ==================== Служебное ====================

    /// <summary>Урезанное движение: ручной переключатель, системная настройка или слабый рендер-уровень.</summary>
    private static bool ReducedMotion =>
        AppSettings.Current.ReducedMotion
        || !SystemParameters.ClientAreaAnimation
        || (RenderCapability.Tier >> 16) < 2;

    private static Duration Ms(double milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    private static Storyboard NewStoryboard() => new() { FillBehavior = FillBehavior.Stop };

    private static DoubleAnimation Anim(double from, double to, double milliseconds, IEasingFunction? ease, double beginMs = 0)
    {
        var animation = new DoubleAnimation(from, to, Ms(milliseconds))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        if (beginMs > 0) animation.BeginTime = TimeSpan.FromMilliseconds(beginMs);
        return animation;
    }

    private static ColorAnimation Anim(Color from, Color to, double milliseconds, IEasingFunction? ease, double beginMs = 0)
    {
        var animation = new ColorAnimation(from, to, Ms(milliseconds))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        if (beginMs > 0) animation.BeginTime = TimeSpan.FromMilliseconds(beginMs);
        return animation;
    }

    private static DoubleAnimationUsingKeyFrames Keys(double from, params (double AtMs, double Value)[] frames)
    {
        double total = frames.Length == 0 ? 0 : frames[^1].AtMs;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = Ms(total),
            FillBehavior = FillBehavior.Stop,
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach ((double atMs, double value) in frames)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(atMs)), EaseSineInOut));
        }
        return animation;
    }

    private static void Track(Storyboard storyboard, AnimationTimeline animation, DependencyObject target, DependencyProperty property)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }

    private void StopSb(ref Storyboard? storyboard)
    {
        Storyboard? sb = storyboard;
        storyboard = null;
        if (sb is null) return;
        try { sb.Stop(this); }
        catch (InvalidOperationException) { /* раскадровка не была запущена в этой области */ }
    }

    private void StopTransient()
    {
        _gen++;
        StopSb(ref _armSb);
        StopSb(ref _igniteSb);
        StopSb(ref _offSb);
        StopSb(ref _faultSb);
        StopSb(ref _captionSb);
        StopSb(ref _odometerSb);
    }

    private void StopEverything()
    {
        StopTransient();
        StopSb(ref _pressSb);
        StopRunLoops();
        _pressed = false;
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ZapretGui.Core;

namespace ZapretGui.Controls;

/// <summary>
/// Присоединённые свойства с типовыми анимациями §7, чтобы страницы не повторяли раскадровки.
/// Любое свойство, навешенное на неподходящий элемент, молча ничего не делает.
/// </summary>
public static class Fx
{
    // ── эталонные цвета §2 на случай, если ресурс темы недоступен ────────────────
    private static readonly Color FallbackHairlineWeak = Color.FromRgb(0x15, 0x1A, 0x21);
    private static readonly Color FallbackHairlineStrong = Color.FromRgb(0x26, 0x2D, 0x37);
    private static readonly Color FallbackAccentMid = Color.FromRgb(0x29, 0xC4, 0xFA);

    private static readonly CubicEase EaseOutCubic = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly QuadraticEase EaseOutQuad = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan RevealStagger = TimeSpan.FromMilliseconds(22);
    private static readonly TimeSpan HoverInDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HoverOutDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReducedDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan PressDownDuration = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan PressUpDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TickShiftDuration = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan TickColorDuration = TimeSpan.FromMilliseconds(400);

    private const int RevealIndexCap = 13;
    private const double HoverLift = -2.0;
    private const double PressScaleTo = 0.976;
    private const string OverlayPartName = "PART_HoverOverlay";

    /// <summary>Глобальный режим пониженной анимации (§7, последняя строка).</summary>
    public static bool ReducedMotion { get; set; }

    /// <summary>Пересчитывает <see cref="ReducedMotion"/> по настройке, системе и классу GPU.</summary>
    public static void DetectReducedMotion()
    {
        var reduced = false;

        try { reduced |= AppSettings.Current.ReducedMotion; }
        catch { /* настройки недоступны — не повод падать */ }

        try { reduced |= !SystemParameters.ClientAreaAnimation; }
        catch { }

        try { reduced |= (RenderCapability.Tier >> 16) < 2; }
        catch { }

        ReducedMotion = reduced;
    }

    // ── Reveal (ListStagger, §7) ─────────────────────────────────────────────────

    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
        "Reveal", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnRevealChanged));

    public static bool GetReveal(DependencyObject obj) => (bool)obj.GetValue(RevealProperty);

    public static void SetReveal(DependencyObject obj, bool value) => obj.SetValue(RevealProperty, value);

    public static readonly DependencyProperty RevealIndexProperty = DependencyProperty.RegisterAttached(
        "RevealIndex", typeof(int), typeof(Fx), new PropertyMetadata(0));

    public static int GetRevealIndex(DependencyObject obj) => (int)obj.GetValue(RevealIndexProperty);

    public static void SetRevealIndex(DependencyObject obj, int value) => obj.SetValue(RevealIndexProperty, value);

    private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        fe.Loaded -= OnRevealLoaded;

        if (e.NewValue is not true)
            return;

        fe.Loaded += OnRevealLoaded;

        if (fe.IsLoaded)
            PlayReveal(fe);
        else if (!ReducedMotion)
            fe.Opacity = 0; // без этого строка мигает до старта раскадровки
    }

    private static void OnRevealLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            PlayReveal(fe);
    }

    private static void PlayReveal(FrameworkElement fe)
    {
        try
        {
            if (ReducedMotion)
            {
                fe.Opacity = 1;
                return;
            }

            var translate = EnsureTranslate(fe);
            if (translate is null)
            {
                fe.Opacity = 1;
                return;
            }

            var index = Math.Max(0, Math.Min(GetRevealIndex(fe), RevealIndexCap));
            var begin = TimeSpan.FromTicks(RevealStagger.Ticks * index);

            fe.Opacity = 0;
            translate.X = -12;

            Run(fe, UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(RevealDuration)) { EasingFunction = EaseOutCubic, BeginTime = begin },
                1.0);

            Run(translate, TranslateTransform.XProperty,
                new DoubleAnimation(-12, 0, new Duration(RevealDuration)) { EasingFunction = EaseOutCubic, BeginTime = begin },
                0.0);
        }
        catch
        {
            fe.Opacity = 1;
        }
    }

    // ── HoverLift (CardHoverLift, §7) ────────────────────────────────────────────

    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnHoverLiftChanged));

    public static bool GetHoverLift(DependencyObject obj) => (bool)obj.GetValue(HoverLiftProperty);

    public static void SetHoverLift(DependencyObject obj, bool value) => obj.SetValue(HoverLiftProperty, value);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        fe.MouseEnter -= OnHoverEnter;
        fe.MouseLeave -= OnHoverLeave;

        if (e.NewValue is not true)
            return;

        fe.MouseEnter += OnHoverEnter;
        fe.MouseLeave += OnHoverLeave;
    }

    private static void OnHoverEnter(object sender, MouseEventArgs e) => PlayHover(sender as FrameworkElement, true);

    private static void OnHoverLeave(object sender, MouseEventArgs e) => PlayHover(sender as FrameworkElement, false);

    private static void PlayHover(FrameworkElement? fe, bool over)
    {
        if (fe is null)
            return;

        try
        {
            var duration = ReducedMotion ? ReducedDuration : (over ? HoverInDuration : HoverOutDuration);
            IEasingFunction? ease = ReducedMotion ? null : EaseOutCubic;

            var overlay = ResolveOverlay(fe);
            if (overlay is not null)
            {
                var to = over ? 1.0 : 0.0;
                Run(overlay, UIElement.OpacityProperty,
                    new DoubleAnimation(overlay.Opacity, to, new Duration(duration)) { EasingFunction = ease }, to);
            }

            var border = EnsureLocalBorderBrush(fe);
            if (border is not null)
            {
                var rest = GetRestBorderColor(fe) ?? ResColor(fe, "ColorHairlineWeak", FallbackHairlineWeak);
                var to = over ? ResColor(fe, "ColorHairlineStrong", FallbackHairlineStrong) : rest;
                Run(border, SolidColorBrush.ColorProperty,
                    new ColorAnimation(border.Color, to, new Duration(duration)) { EasingFunction = ease }, to);
            }

            if (ReducedMotion)
                return; // §7: подсветка остаётся, подъём отключён

            var translate = EnsureTranslate(fe);
            if (translate is not null)
            {
                var to = over ? HoverLift : 0.0;
                Run(translate, TranslateTransform.YProperty,
                    new DoubleAnimation(translate.Y, to, new Duration(duration)) { EasingFunction = ease }, to);
            }
        }
        catch
        {
            // элемент не поддерживает нужные слои — просто нет эффекта
        }
    }

    // ── PressScale (ButtonPress, §7) ─────────────────────────────────────────────

    public static readonly DependencyProperty PressScaleProperty = DependencyProperty.RegisterAttached(
        "PressScale", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnPressScaleChanged));

    public static bool GetPressScale(DependencyObject obj) => (bool)obj.GetValue(PressScaleProperty);

    public static void SetPressScale(DependencyObject obj, bool value) => obj.SetValue(PressScaleProperty, value);

    private static void OnPressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        fe.PreviewMouseLeftButtonDown -= OnPressDown;
        fe.PreviewMouseLeftButtonUp -= OnPressUp;
        fe.MouseLeave -= OnPressCancel;
        fe.LostMouseCapture -= OnPressLostCapture;

        if (e.NewValue is not true)
            return;

        fe.PreviewMouseLeftButtonDown += OnPressDown;
        fe.PreviewMouseLeftButtonUp += OnPressUp;
        fe.MouseLeave += OnPressCancel;
        fe.LostMouseCapture += OnPressLostCapture;
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e) => PlayPress(sender as FrameworkElement, true);

    private static void OnPressUp(object sender, MouseButtonEventArgs e) => PlayPress(sender as FrameworkElement, false);

    private static void OnPressCancel(object sender, MouseEventArgs e) => PlayPress(sender as FrameworkElement, false);

    private static void OnPressLostCapture(object sender, MouseEventArgs e) => PlayPress(sender as FrameworkElement, false);

    private static void PlayPress(FrameworkElement? fe, bool down)
    {
        if (fe is null)
            return;

        try
        {
            var scale = EnsureScale(fe);
            if (scale is null)
                return;

            fe.RenderTransformOrigin = new Point(0.5, 0.5);

            var to = down ? PressScaleTo : 1.0;
            var duration = down ? PressDownDuration : PressUpDuration;

            Run(scale, ScaleTransform.ScaleXProperty,
                new DoubleAnimation(scale.ScaleX, to, new Duration(duration)) { EasingFunction = EaseOutQuad }, to);
            Run(scale, ScaleTransform.ScaleYProperty,
                new DoubleAnimation(scale.ScaleY, to, new Duration(duration)) { EasingFunction = EaseOutQuad }, to);
        }
        catch
        {
        }
    }

    // ── TickOnChange (ValueTick, §7) ─────────────────────────────────────────────

    public static readonly DependencyProperty TickOnChangeProperty = DependencyProperty.RegisterAttached(
        "TickOnChange", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnTickOnChangeChanged));

    public static bool GetTickOnChange(DependencyObject obj) => (bool)obj.GetValue(TickOnChangeProperty);

    public static void SetTickOnChange(DependencyObject obj, bool value) => obj.SetValue(TickOnChangeProperty, value);

    private static readonly DependencyProperty TickBaseColorProperty = DependencyProperty.RegisterAttached(
        "TickBaseColor", typeof(object), typeof(Fx), new PropertyMetadata(null));

    private static void OnTickOnChangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
            return;

        tb.Loaded -= OnTickLoaded;
        tb.Unloaded -= OnTickUnloaded;
        DetachTextWatcher(tb, OnTickTextChanged);

        if (e.NewValue is not true)
            return;

        tb.Loaded += OnTickLoaded;
        tb.Unloaded += OnTickUnloaded;

        if (tb.IsLoaded)
            AttachTextWatcher(tb, OnTickTextChanged);
    }

    private static void OnTickLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb)
            AttachTextWatcher(tb, OnTickTextChanged);
    }

    private static void OnTickUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb)
            DetachTextWatcher(tb, OnTickTextChanged);
    }

    private static void OnTickTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock tb || ReducedMotion || GetSuppressText(tb))
            return;

        try
        {
            var brush = EnsureLocalForeground(tb);
            if (brush is not null)
            {
                var baseColor = tb.GetValue(TickBaseColorProperty) is Color stored ? stored : brush.Color;
                tb.SetValue(TickBaseColorProperty, baseColor);

                var accent = ResColor(tb, "ColorAccentMid", FallbackAccentMid);
                var keys = new ColorAnimationUsingKeyFrames { Duration = new Duration(TickColorDuration) };
                keys.KeyFrames.Add(new LinearColorKeyFrame(baseColor, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                keys.KeyFrames.Add(new LinearColorKeyFrame(accent, KeyTime.FromPercent(0.15)));
                keys.KeyFrames.Add(new LinearColorKeyFrame(baseColor, KeyTime.FromPercent(1.0)));
                Run(brush, SolidColorBrush.ColorProperty, keys, baseColor);
            }

            var translate = EnsureTranslate(tb);
            if (translate is not null)
            {
                Run(translate, TranslateTransform.YProperty,
                    new DoubleAnimation(-2, 0, new Duration(TickShiftDuration)) { EasingFunction = EaseOutQuad }, 0.0);
            }
        }
        catch
        {
        }
    }

    // ── StickyScroll ─────────────────────────────────────────────────────────────

    public static readonly DependencyProperty StickyScrollProperty = DependencyProperty.RegisterAttached(
        "StickyScroll", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnStickyScrollChanged));

    public static bool GetStickyScroll(DependencyObject obj) => (bool)obj.GetValue(StickyScrollProperty);

    public static void SetStickyScroll(DependencyObject obj, bool value) => obj.SetValue(StickyScrollProperty, value);

    private static readonly DependencyProperty StickyAtEndProperty = DependencyProperty.RegisterAttached(
        "StickyAtEnd", typeof(bool), typeof(Fx), new PropertyMetadata(true));

    private static readonly DependencyProperty StickyHostProperty = DependencyProperty.RegisterAttached(
        "StickyHost", typeof(ScrollViewer), typeof(Fx), new PropertyMetadata(null));

    private static void OnStickyScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        fe.Loaded -= OnStickyLoaded;

        if (fe.GetValue(StickyHostProperty) is ScrollViewer old)
        {
            old.ScrollChanged -= OnStickyScrolled;
            fe.SetValue(StickyHostProperty, null);
        }

        if (e.NewValue is not true)
            return;

        fe.Loaded += OnStickyLoaded;

        if (fe.IsLoaded)
            AttachSticky(fe);
    }

    private static void OnStickyLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            AttachSticky(fe);
    }

    private static void AttachSticky(FrameworkElement fe)
    {
        try
        {
            if (fe.GetValue(StickyHostProperty) is ScrollViewer)
                return;

            var sv = FindScrollViewer(fe);
            if (sv is null)
                return;

            fe.SetValue(StickyHostProperty, sv);
            sv.SetValue(StickyAtEndProperty, true);
            sv.ScrollChanged += OnStickyScrolled;
            sv.ScrollToEnd();
        }
        catch
        {
        }
    }

    private static void OnStickyScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv)
            return;

        try
        {
            // рост содержимого — доводим вниз, если пользователь не ушёл вверх сам
            if (Math.Abs(e.ExtentHeightChange) > 0.5 || Math.Abs(e.ViewportHeightChange) > 0.5)
            {
                if ((bool)sv.GetValue(StickyAtEndProperty))
                    sv.ScrollToVerticalOffset(sv.ScrollableHeight);
                return;
            }

            if (Math.Abs(e.VerticalChange) > 0.01)
                sv.SetValue(StickyAtEndProperty, IsScrolledToEnd(sv));
        }
        catch
        {
        }
    }

    /// <summary>Прокручен ли просмотрщик до низа (с допуском 1 DIP).</summary>
    public static bool IsScrolledToEnd(ScrollViewer sv)
    {
        if (sv is null)
            return true;

        return sv.ScrollableHeight <= 0 || sv.VerticalOffset >= sv.ScrollableHeight - 1.0;
    }

    /// <summary>Находит <see cref="ScrollViewer"/> в визуальном дереве (или сам элемент).</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is null)
            return null;

        if (root is ScrollViewer direct)
            return direct;

        try
        {
            if (root is FrameworkElement fe)
                fe.ApplyTemplate();

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found is not null)
                    return found;
            }
        }
        catch
        {
        }

        return null;
    }

    // ── Tracking (§3: в WPF нет letter-spacing) ──────────────────────────────────

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.RegisterAttached(
        "Tracking", typeof(double), typeof(Fx), new PropertyMetadata(0.0, OnTrackingChanged));

    public static double GetTracking(DependencyObject obj) => (double)obj.GetValue(TrackingProperty);

    public static void SetTracking(DependencyObject obj, double value) => obj.SetValue(TrackingProperty, value);

    private static readonly DependencyProperty TrackingSourceProperty = DependencyProperty.RegisterAttached(
        "TrackingSource", typeof(string), typeof(Fx), new PropertyMetadata(null));

    private static readonly DependencyProperty SuppressTextProperty = DependencyProperty.RegisterAttached(
        "SuppressText", typeof(bool), typeof(Fx), new PropertyMetadata(false));

    private static bool GetSuppressText(DependencyObject obj) => (bool)obj.GetValue(SuppressTextProperty);

    private static void OnTrackingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
            return;

        tb.Loaded -= OnTrackingLoaded;
        tb.Unloaded -= OnTrackingUnloaded;
        DetachTextWatcher(tb, OnTrackingTextChanged);

        var tracking = e.NewValue is double v ? v : 0.0;
        if (double.IsNaN(tracking) || tracking <= 0)
        {
            RestoreTrackingSource(tb);
            return;
        }

        tb.Loaded += OnTrackingLoaded;
        tb.Unloaded += OnTrackingUnloaded;
        AttachTextWatcher(tb, OnTrackingTextChanged);

        tb.SetValue(TrackingSourceProperty, tb.Text);
        ApplyTracking(tb);
    }

    private static void OnTrackingLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb)
        {
            AttachTextWatcher(tb, OnTrackingTextChanged);
            ApplyTracking(tb);
        }
    }

    private static void OnTrackingUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb)
            DetachTextWatcher(tb, OnTrackingTextChanged);
    }

    private static void OnTrackingTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock tb || GetSuppressText(tb))
            return;

        tb.SetValue(TrackingSourceProperty, tb.Text);
        ApplyTracking(tb);
    }

    private static void RestoreTrackingSource(TextBlock tb)
    {
        if (tb.GetValue(TrackingSourceProperty) is not string source || string.Equals(source, tb.Text, StringComparison.Ordinal))
            return;

        tb.SetValue(SuppressTextProperty, true);
        try { tb.Text = source; }
        finally { tb.SetValue(SuppressTextProperty, false); }
    }

    private static void ApplyTracking(TextBlock tb)
    {
        try
        {
            if (tb.Inlines.Count > 1)
                return; // размеченный текст не трогаем — Text его уничтожит

            var source = tb.GetValue(TrackingSourceProperty) as string ?? tb.Text;
            var spaced = Space(source, GetTracking(tb));
            if (string.Equals(spaced, tb.Text, StringComparison.Ordinal))
                return;

            tb.SetValue(SuppressTextProperty, true);
            try { tb.Text = spaced; }
            finally { tb.SetValue(SuppressTextProperty, false); }
        }
        catch
        {
        }
    }

    private static string Space(string? text, double tracking)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 24 || tracking < 40)
            return text ?? string.Empty;

        foreach (var ch in text)
        {
            if (char.IsLower(ch))
                return text; // только ЗАГЛАВНЫЕ подписи
        }

        // U+2009 ≈ 0.2 em, U+200A ≈ 0.1 em — ближайшие доступные шаги трекинга
        var gap = tracking >= 100 ? ' ' : ' ';
        var sb = new StringBuilder(text.Length * 2);

        for (var i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i]) && !char.IsWhiteSpace(text[i + 1]))
                sb.Append(gap);
        }

        return sb.ToString();
    }

    // ── общая машинерия ──────────────────────────────────────────────────────────

    private static readonly DependencyProperty FxTransformProperty = DependencyProperty.RegisterAttached(
        "FxTransform", typeof(TransformGroup), typeof(Fx), new PropertyMetadata(null));

    private static readonly DependencyProperty FxOverlayProperty = DependencyProperty.RegisterAttached(
        "FxOverlay", typeof(UIElement), typeof(Fx), new PropertyMetadata(null));

    private static readonly DependencyProperty RestBorderColorProperty = DependencyProperty.RegisterAttached(
        "RestBorderColor", typeof(object), typeof(Fx), new PropertyMetadata(null));

    /// <summary>Последняя запущенная анимация по паре (элемент, свойство) — чтобы устаревший Completed не сбил новую.</summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, object>> Running = new();

    private static void Run(IAnimatable target, DependencyProperty property, AnimationTimeline animation, object finalValue)
    {
        if (target is not DependencyObject host)
            return;

        animation.FillBehavior = FillBehavior.Stop;

        var map = Running.GetOrCreateValue(host);
        lock (map)
            map[property] = animation;

        animation.Completed += (_, _) =>
        {
            try
            {
                lock (map)
                {
                    if (!map.TryGetValue(property, out var current) || !ReferenceEquals(current, animation))
                        return;
                    map.Remove(property);
                }

                // §7: FillBehavior=Stop, конечное значение фиксируем сами
                target.BeginAnimation(property, null);
                host.SetValue(property, finalValue);
            }
            catch
            {
            }
        };

        target.BeginAnimation(property, animation);
    }

    private static TransformGroup? EnsureGroup(FrameworkElement fe)
    {
        try
        {
            if (fe.GetValue(FxTransformProperty) is TransformGroup cached && ReferenceEquals(fe.RenderTransform, cached))
                return cached;

            var group = new TransformGroup();

            var existing = fe.RenderTransform;
            if (existing is not null && existing != Transform.Identity && !existing.Value.IsIdentity)
                group.Children.Add(existing.IsFrozen ? (Transform)existing.CloneCurrentValue() : existing);

            group.Children.Add(new ScaleTransform(1, 1));
            group.Children.Add(new TranslateTransform(0, 0));

            fe.RenderTransform = group;
            fe.SetValue(FxTransformProperty, group);
            return group;
        }
        catch
        {
            return null;
        }
    }

    private static ScaleTransform? EnsureScale(FrameworkElement fe)
    {
        var group = EnsureGroup(fe);
        return group?.Children[group.Children.Count - 2] as ScaleTransform;
    }

    private static TranslateTransform? EnsureTranslate(FrameworkElement fe)
    {
        var group = EnsureGroup(fe);
        return group?.Children[group.Children.Count - 1] as TranslateTransform;
    }

    private static UIElement? ResolveOverlay(FrameworkElement fe)
    {
        if (fe.GetValue(FxOverlayProperty) is UIElement cached)
            return cached;

        var found = FindOverlayPart(fe, 0);
        if (found is null && fe is Border { Child: Panel panel } border)
        {
            var rect = new Rectangle
            {
                Fill = fe.TryFindResource("BrushCardHoverOverlay") as Brush ?? Application.Current?.TryFindResource("BrushCardHoverOverlay") as Brush,
                IsHitTestVisible = false,
                Opacity = 0,
                RadiusX = border.CornerRadius.TopLeft,
                RadiusY = border.CornerRadius.TopLeft,
            };

            if (panel is Grid)
            {
                Grid.SetRowSpan(rect, 99);
                Grid.SetColumnSpan(rect, 99);
            }

            panel.Children.Insert(0, rect);
            found = rect;
        }

        if (found is not null)
            fe.SetValue(FxOverlayProperty, found);

        return found;
    }

    private static UIElement? FindOverlayPart(DependencyObject root, int depth)
    {
        if (depth > 4)
            return null;

        try
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement { Name: OverlayPartName } element)
                    return element;

                var nested = FindOverlayPart(child, depth + 1);
                if (nested is not null)
                    return nested;
            }
        }
        catch
        {
        }

        return null;
    }

    private static SolidColorBrush? EnsureLocalBorderBrush(FrameworkElement fe)
    {
        Brush? current = fe switch
        {
            Border b => b.BorderBrush,
            Control c => c.BorderBrush,
            _ => null,
        };

        if (current is not SolidColorBrush solid)
            return null;

        if (fe.GetValue(RestBorderColorProperty) is not Color)
            fe.SetValue(RestBorderColorProperty, solid.Color);

        if (!solid.IsFrozen && ReferenceEquals(fe.GetValue(FxBorderBrushProperty), solid))
            return solid;

        // нельзя анимировать общую замороженную кисть — заводим личную копию
        var local = new SolidColorBrush(solid.Color);
        switch (fe)
        {
            case Border b: b.BorderBrush = local; break;
            case Control c: c.BorderBrush = local; break;
        }

        fe.SetValue(FxBorderBrushProperty, local);
        return local;
    }

    private static readonly DependencyProperty FxBorderBrushProperty = DependencyProperty.RegisterAttached(
        "FxBorderBrush", typeof(SolidColorBrush), typeof(Fx), new PropertyMetadata(null));

    private static Color? GetRestBorderColor(DependencyObject obj)
        => obj.GetValue(RestBorderColorProperty) is Color c ? c : null;

    private static readonly DependencyProperty FxForegroundProperty = DependencyProperty.RegisterAttached(
        "FxForeground", typeof(SolidColorBrush), typeof(Fx), new PropertyMetadata(null));

    private static SolidColorBrush? EnsureLocalForeground(TextBlock tb)
    {
        if (tb.Foreground is not SolidColorBrush solid)
            return null;

        if (!solid.IsFrozen && ReferenceEquals(tb.GetValue(FxForegroundProperty), solid))
            return solid;

        var local = new SolidColorBrush(solid.Color);
        tb.Foreground = local;
        tb.SetValue(FxForegroundProperty, local);
        return local;
    }

    private static void AttachTextWatcher(TextBlock tb, EventHandler handler)
    {
        try
        {
            var dpd = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            dpd?.RemoveValueChanged(tb, handler);
            dpd?.AddValueChanged(tb, handler);
        }
        catch
        {
        }
    }

    private static void DetachTextWatcher(TextBlock tb, EventHandler handler)
    {
        try
        {
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))?.RemoveValueChanged(tb, handler);
        }
        catch
        {
        }
    }

    private static Color ResColor(FrameworkElement fe, string key, Color fallback)
    {
        try
        {
            var value = fe.TryFindResource(key) ?? Application.Current?.TryFindResource(key);
            return value switch
            {
                Color c => c,
                SolidColorBrush b => b.Color,
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
            freezable.Freeze();
        return freezable;
    }
}

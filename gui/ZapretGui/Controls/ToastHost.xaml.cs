using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZapretGui.Core;

namespace ZapretGui.Controls;

/// <summary>
/// Стек тостов в правом нижнем углу контент-хоста (§9). Появление снизу вверх,
/// не больше четырёх штук, выдержка 4000 мс (для ошибок — 8000 мс), пауза при наведении.
/// </summary>
public partial class ToastHost : System.Windows.Controls.UserControl
{
    private const int MaxVisible = 4;
    private const int DwellMs = 4000;
    private const int DwellErrorMs = 8000;
    private const int InMs = 200;
    private const int OutMs = 140;
    private const int ReducedMs = 120;

    private readonly List<ToastEntry> _live = new();

    public ToastHost()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Живой экземпляр хоста; выставляется в Loaded.</summary>
    public static ToastHost? Current { get; private set; }

    /// <summary>Показать тост из любого потока; без хоста сообщение молча теряется.</summary>
    public static void Post(string message, ToastKind kind = ToastKind.Info)
    {
        ToastHost? host = Current;
        if (host is null)
        {
            return;
        }

        if (host.Dispatcher.CheckAccess())
        {
            host.Show(message, kind);
        }
        else
        {
            host.Dispatcher.BeginInvoke(new Action(() => host.Show(message, kind)));
        }
    }

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Самый старый уходит первым, чтобы стек не перерастал четыре карточки.
        while (_live.Count >= MaxVisible)
        {
            Dismiss(_live[0]);
        }

        ToastEntry entry = BuildToast(message, kind);
        Stack.Children.Add(entry.Root);
        _live.Add(entry);

        AnimateIn(entry);
        StartDwell(entry, TimeSpan.FromMilliseconds(kind == ToastKind.Error ? DwellErrorMs : DwellMs));
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Current = this;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        foreach (ToastEntry entry in _live)
        {
            entry.Timer.Stop();
        }

        _live.Clear();
        Stack.Children.Clear();
    }

    // ---------------------------------------------------------------- сборка

    private ToastEntry BuildToast(string message, ToastKind kind)
    {
        Geometry icon = IconFor(kind);

        var move = new TranslateTransform();

        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0,
            RenderTransform = move,
        };

        // Тень висит на отдельном подслое: §3 запрещает текст внутри элемента с Effect.
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ResBrush("BrushSurfaceOverlay"),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = System.Windows.Media.Color.FromArgb(0xCC, 0, 0, 0),
                BlurRadius = 24,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.55,
                RenderingBias = RenderingBias.Performance,
            },
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ResBrush("BrushSurfaceOverlay"),
            BorderBrush = ResBrush("BrushHairlineStrong"),
            BorderThickness = new Thickness(1),
            MinWidth = 320,
            MaxWidth = 380,
            MinHeight = 56,
            SnapsToDevicePixels = true,
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Цветная планка слева по типу сообщения.
        var bar = new Border { CornerRadius = new CornerRadius(12, 0, 0, 12) };
        ApplyKindBrush(bar, Border.BackgroundProperty, kind);
        Grid.SetColumn(bar, 0);
        layout.Children.Add(bar);

        var row = new Grid { Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new System.Windows.Shapes.Path
        {
            Data = icon,
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        ApplyKindBrush(glyph, Shape.StrokeProperty, kind);
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        var text = new TextBlock
        {
            Text = message,
            FontFamily = (FontFamily)FindResource("FontUI"),
            FontSize = 12.5,
            LineHeight = 17,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResBrush("BrushTextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var close = new Button
        {
            Style = (Style)FindResource("ToastCloseButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource("IconCloseStroke"),
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform,
                Stroke = ResBrush("BrushTextSecondary"),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = null,
            },
        };
        Grid.SetColumn(close, 2);
        row.Children.Add(close);

        Grid.SetColumn(row, 1);
        layout.Children.Add(row);
        card.Child = layout;

        root.Children.Add(shadow);
        root.Children.Add(card);

        var entry = new ToastEntry(root, move);

        close.Click += (_, _) => Dismiss(entry);
        root.MouseEnter += (_, _) => HoldDwell(entry);
        root.MouseLeave += (_, _) => ResumeDwell(entry);

        return entry;
    }

    private Geometry IconFor(ToastKind kind) => (Geometry)FindResource(kind switch
    {
        ToastKind.Success => "IconCheckStroke",
        ToastKind.Warning => "IconAlertStroke",
        ToastKind.Error => "IconCloseStroke",
        _ => "IconInfoStroke",
    });

    private void ApplyKindBrush(FrameworkElement element, DependencyProperty property, ToastKind kind)
    {
        switch (kind)
        {
            case ToastKind.Success:
                element.SetValue(property, ResBrush("BrushSuccess"));
                break;
            case ToastKind.Warning:
                element.SetValue(property, ResBrush("BrushWarning"));
                break;
            case ToastKind.Error:
                element.SetValue(property, ResBrush("BrushDanger"));
                break;
            default:
                // Информационный тост окрашивается акцентом — он подменяется на лету.
                element.SetResourceReference(property, "BrushAccentMid");
                break;
        }
    }

    private Brush ResBrush(string key) => (Brush)FindResource(key);

    // ------------------------------------------------------------- выдержка

    private void StartDwell(ToastEntry entry, TimeSpan dwell)
    {
        entry.Remaining = dwell;
        entry.Timer.Interval = dwell;
        entry.Timer.Tick += (_, _) =>
        {
            entry.Timer.Stop();
            Dismiss(entry);
        };
        entry.StartedAt = DateTime.UtcNow;
        entry.Timer.Start();
    }

    private static void HoldDwell(ToastEntry entry)
    {
        if (entry.Dismissing || !entry.Timer.IsEnabled)
        {
            return;
        }

        entry.Timer.Stop();
        TimeSpan spent = DateTime.UtcNow - entry.StartedAt;
        entry.Remaining = entry.Remaining - spent;
        if (entry.Remaining < TimeSpan.FromMilliseconds(600))
        {
            entry.Remaining = TimeSpan.FromMilliseconds(600);
        }
    }

    private static void ResumeDwell(ToastEntry entry)
    {
        if (entry.Dismissing || entry.Timer.IsEnabled)
        {
            return;
        }

        entry.Timer.Interval = entry.Remaining;
        entry.StartedAt = DateTime.UtcNow;
        entry.Timer.Start();
    }

    // ------------------------------------------------------------- движение

    private void AnimateIn(ToastEntry entry)
    {
        bool reduced = Fx.ReducedMotion;
        var board = new Storyboard();

        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(reduced ? ReducedMs : InMs)))
        {
            EasingFunction = reduced ? null : new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(fade, entry.Root);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        board.Children.Add(fade);

        if (!reduced)
        {
            entry.Move.Y = 16;
            var slide = new DoubleAnimation(16, 0, new Duration(TimeSpan.FromMilliseconds(InMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            Storyboard.SetTarget(slide, entry.Move);
            Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.YProperty));
            board.Children.Add(slide);
        }

        board.FillBehavior = FillBehavior.Stop;

        // Stop() тоже поднимает Completed — сверяемся, что раскадровка ещё «наша», иначе
        // повторный Remove на уже снятой раскадровке бросит исключение.
        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(entry.Board, board))
            {
                return;
            }

            entry.Board = null;
            board.Remove(entry.Root);
            entry.Root.Opacity = 1;
            entry.Move.Y = 0;
        };

        entry.Board = board;
        board.Begin(entry.Root, isControllable: true);
    }

    private void Dismiss(ToastEntry entry)
    {
        if (entry.Dismissing)
        {
            return;
        }

        entry.Dismissing = true;
        entry.Timer.Stop();
        _live.Remove(entry);

        Storyboard? previous = entry.Board;
        entry.Board = null;
        if (previous is not null)
        {
            previous.Stop(entry.Root);
            previous.Remove(entry.Root);
        }

        entry.Root.Opacity = 1;
        entry.Move.Y = 0;

        bool reduced = Fx.ReducedMotion;
        var board = new Storyboard();

        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(reduced ? ReducedMs : OutMs)))
        {
            EasingFunction = reduced ? null : new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(fade, entry.Root);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        board.Children.Add(fade);

        if (!reduced)
        {
            var slide = new DoubleAnimation(0, -8, new Duration(TimeSpan.FromMilliseconds(OutMs)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop,
            };
            Storyboard.SetTarget(slide, entry.Move);
            Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.YProperty));
            board.Children.Add(slide);
        }

        board.FillBehavior = FillBehavior.Stop;
        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(entry.Board, board))
            {
                return;
            }

            entry.Board = null;
            board.Remove(entry.Root);
            entry.Root.Opacity = 0;
            Stack.Children.Remove(entry.Root);
        };

        entry.Board = board;
        board.Begin(entry.Root, isControllable: true);
    }

    private sealed class ToastEntry
    {
        public ToastEntry(Grid root, TranslateTransform move)
        {
            Root = root;
            Move = move;
            Timer = new DispatcherTimer(DispatcherPriority.Normal, root.Dispatcher);
        }

        public Grid Root { get; }

        public TranslateTransform Move { get; }

        public DispatcherTimer Timer { get; }

        public Storyboard? Board { get; set; }

        public TimeSpan Remaining { get; set; }

        public DateTime StartedAt { get; set; }

        public bool Dismissing { get; set; }
    }
}

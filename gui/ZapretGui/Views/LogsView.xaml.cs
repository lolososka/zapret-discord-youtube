using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ZapretGui.Controls;
using ZapretGui.Core;

namespace ZapretGui.Views;

/// <summary>
/// Страница «Журнал»: чипы уровня, поиск, виртуализированный список строк,
/// липкая автопрокрутка с пилюлей «К последним», сохранение и копирование.
/// </summary>
public partial class LogsView : UserControl, INotifyPropertyChanged
{
    private readonly AppState _state = AppState.Instance;

    private ListCollectionView? _rows;
    private ScrollViewer? _scroll;

    private string _search = string.Empty;
    private string _level = "all";

    private bool _pillShown;
    private Storyboard? _pillSb;

    private string _lineCountText = "0 строк";
    private bool _isEmpty = true;

    // Разделитель разрядов фиксируем сами: культура процесса может быть любой.
    private static readonly NumberFormatInfo GroupedNumbers = new() { NumberGroupSeparator = " ", NumberDecimalDigits = 0 };

    public LogsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>«1 284 строки» — по отфильтрованному представлению.</summary>
    public string LineCountText
    {
        get => _lineCountText;
        private set => Set(ref _lineCountText, value);
    }

    /// <summary>Отфильтрованный журнал пуст — показываем пустое состояние §9.</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    // ---------- Жизненный цикл ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_rows is null)
        {
            // Собственный вид, а не GetDefaultView: фильтр журнала не должен протекать наружу.
            _rows = new ListCollectionView(_state.Log) { Filter = FilterRow };
            List.ItemsSource = _rows;
            ((INotifyCollectionChanged)_rows).CollectionChanged += OnRowsChanged;
        }

        if (_scroll is null)
        {
            _scroll = Fx.FindScrollViewer(List);
            if (_scroll is not null)
                _scroll.ScrollChanged += OnScrollChanged;
        }

        ApplyFilter();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_scroll is not null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
            _scroll = null;
        }

        _pillSb?.Stop(this);
        _pillSb = null;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCounters();
        UpdatePill();
    }

    // ---------- Фильтрация ----------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _search = Search.Text.Trim();
        ApplyFilter();
    }

    private void OnChipChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
            _level = tag;

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_rows is null) return;   // Checked прилетает ещё из InitializeComponent

        _rows.Refresh();
        UpdateCounters();

        if (AutoScrollToggle.IsChecked == true)
            _scroll?.ScrollToEnd();

        UpdatePill();
    }

    private bool FilterRow(object item)
    {
        if (item is not LogLine line) return false;

        if (!LevelMatches(line.Level)) return false;
        if (_search.Length == 0) return true;

        return line.Text.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    private bool LevelMatches(LogLevel level) => _level switch
    {
        "warn" => level is LogLevel.Warn or LogLevel.Error,
        "error" => level is LogLevel.Error,
        _ => true,
    };

    private void UpdateCounters()
    {
        int n = _rows?.Count ?? 0;
        IsEmpty = n == 0;
        LineCountText = n.ToString("N0", GroupedNumbers) + " " + Plural(n, "строка", "строки", "строк");
    }

    // ---------- Липкая прокрутка и пилюля ----------

    private void OnAutoScrollChanged(object sender, RoutedEventArgs e)
    {
        // Checked прилетает ещё из InitializeComponent, когда полей ещё нет — читаем sender.
        if (_rows is null) return;

        // Fx.StickyScroll привязан к этому же переключателю — здесь только доводка вниз.
        if (sender is System.Windows.Controls.Primitives.ToggleButton { IsChecked: true })
            _scroll?.ScrollToEnd();

        UpdatePill();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdatePill();

    private void OnJumpClick(object sender, RoutedEventArgs e)
    {
        _scroll?.ScrollToEnd();
        UpdatePill();
    }

    private void UpdatePill()
    {
        bool detached = _scroll is not null && !IsEmpty && !Fx.IsScrolledToEnd(_scroll);
        SetPill(detached);
    }

    /// <summary>Появление/уход пилюли: FillBehavior=Stop, конечные значения фиксируются в Completed.</summary>
    private void SetPill(bool show)
    {
        if (show == _pillShown) return;
        _pillShown = show;

        _pillSb?.Stop(this);
        _pillSb = null;

        double opacityTo = show ? 1.0 : 0.0;
        double shiftTo = show ? 0.0 : 8.0;

        if (show)
            JumpPill.Visibility = Visibility.Visible;

        bool reduced = Fx.ReducedMotion;
        var duration = new Duration(TimeSpan.FromMilliseconds(reduced ? 120 : show ? 180 : 140));
        IEasingFunction? ease = reduced
            ? null
            : show
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new QuadraticEase { EasingMode = EasingMode.EaseIn };

        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };
        Add(sb, JumpPill, "Opacity", opacityTo, duration, ease);

        if (reduced)
            PillShift.Y = 0;
        else
            Add(sb, PillShift, "Y", shiftTo, duration, ease);

        sb.Completed += (_, _) =>
        {
            JumpPill.Opacity = opacityTo;
            PillShift.Y = reduced ? 0 : shiftTo;

            if (!_pillShown)
                JumpPill.Visibility = Visibility.Collapsed;

            _pillSb = null;
        };

        _pillSb = sb;
        sb.Begin(this, true);
    }

    private static void Add(Storyboard sb, DependencyObject target, string property,
                            double to, Duration duration, IEasingFunction? ease)
    {
        var anim = new DoubleAnimation(to, duration) { EasingFunction = ease };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(property));
        sb.Children.Add(anim);
    }

    // ---------- Сохранение / копирование ----------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_rows is null || _rows.IsEmpty)
        {
            _state.Notify("Журнал пуст — сохранять нечего", ToastKind.Warning);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить журнал",
            Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = "zapret-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildText(all: true), new UTF8Encoding(true));
            _state.Notify("Журнал сохранён", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _state.Notify("Не удалось сохранить журнал: " + ex.Message, ToastKind.Error);
        }
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e) => CopyToClipboard(all: true);

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        CopyToClipboard(all: List.SelectedItems.Count == 0);
        e.Handled = true;
    }

    private void CopyToClipboard(bool all)
    {
        var text = BuildText(all);
        if (text.Length == 0)
        {
            _state.Notify("Журнал пуст — копировать нечего", ToastKind.Warning);
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _state.Notify(all ? "Журнал скопирован" : "Строки скопированы", ToastKind.Success);
        }
        catch
        {
            // Буфер обмена бывает захвачен другим процессом — это не повод падать.
            _state.Notify("Не удалось скопировать журнал", ToastKind.Warning);
        }
    }

    /// <summary>Собирает текст: либо всё отфильтрованное представление, либо выделенные строки.</summary>
    private string BuildText(bool all)
    {
        var sb = new StringBuilder();

        if (all)
        {
            if (_rows is not null)
                foreach (var item in _rows)
                    Append(sb, item as LogLine);
        }
        else
        {
            foreach (var item in List.SelectedItems)
                Append(sb, item as LogLine);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, LogLine? line)
    {
        if (line is null) return;

        sb.Append(line.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
          .Append("  ")
          .Append(LevelTag(line.Level))
          .Append("  ")
          .AppendLine(line.Text);
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Success => "OK  ",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERR ",
        _ => "INFO",
    };

    // ---------- Мелочи ----------

    private static string Plural(int n, string one, string few, string many)
    {
        int mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return many;

        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

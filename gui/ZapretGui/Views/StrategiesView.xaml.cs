using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZapretGui.Controls;
using ZapretGui.Core;

namespace ZapretGui.Views;

/// <summary>
/// Страница «Стратегии»: поиск, чипы-фильтры, виртуализированный список профилей,
/// модалка с полной командной строкой и панель действий по службе.
/// </summary>
public partial class StrategiesView : UserControl, INotifyPropertyChanged
{
    private readonly AppState _state = AppState.Instance;

    private ListCollectionView? _rows;
    private string _search = string.Empty;
    private string _chip = "all";

    // Защита от рикошета: программная установка SelectedItem не должна переписывать AppState.
    private bool _syncing;
    private Storyboard? _modalSb;

    private string _subtitle = string.Empty;
    private bool _isEmpty;

    public StrategiesView()
    {
        InitializeComponent();
        UpdateSubtitle();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>«22 профиля · выбран General ALT 4» — считается по реальным данным.</summary>
    public string Subtitle
    {
        get => _subtitle;
        private set => Set(ref _subtitle, value);
    }

    /// <summary>Список после фильтрации пуст — показываем пустое состояние из §9.</summary>
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
            // Собственный вид, а не GetDefaultView: фильтр не должен протекать на другие страницы.
            _rows = new ListCollectionView(_state.Strategies)
            {
                Filter = FilterRow,
                CustomSort = new StrategyOrder(),
            };
            List.ItemsSource = _rows;
        }

        _state.PropertyChanged += OnStatePropertyChanged;
        _state.StrategyMarksChanged += OnMarksChanged;
        ((INotifyCollectionChanged)_state.Strategies).CollectionChanged += OnStrategiesChanged;

        ApplyFilter();
        UpdateSubtitle();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _state.PropertyChanged -= OnStatePropertyChanged;
        _state.StrategyMarksChanged -= OnMarksChanged;
        ((INotifyCollectionChanged)_state.Strategies).CollectionChanged -= OnStrategiesChanged;

        // Страницу могут выгрузить прямо посреди раскадровки — гасим модалку целиком.
        _modalSb?.Stop(this);
        _modalSb = null;
        ModalWrap.CacheMode = null;
        ModalWrap.Opacity = 0;
        ModalScale.ScaleX = 0.98;
        ModalScale.ScaleY = 0.98;
        ModalSlide.Y = 10;
        Scrim.Opacity = 0;
        ModalHost.Visibility = Visibility.Collapsed;
        ModalCard.DataContext = null;
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.SelectedStrategy):
            case nameof(AppState.SelectedStrategyName):
                UpdateSubtitle();
                SyncSelection();
                break;
        }
    }

    private void OnStrategiesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ApplyFilter();
        UpdateSubtitle();
    }

    /// <summary>Звёздочка или отметка «работала у вас» изменились — порядок строк устарел.</summary>
    private void OnMarksChanged(object? sender, EventArgs e) => ApplyFilter();

    // ---------- Фильтрация ----------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _search = Search.Text.Trim();
        ApplyFilter();
    }

    private void OnChipChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
            _chip = tag;

        ApplyFilter();
    }

    private void OnResetFiltersClick(object sender, RoutedEventArgs e)
    {
        Search.Text = string.Empty;
        ChipAll.IsChecked = true;   // Checked сам вызовет ApplyFilter
        _chip = "all";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_rows is null) return;   // Checked прилетает ещё из InitializeComponent

        // Refresh сбрасывает текущий элемент вида; выделение ListBox не должно утечь в AppState.
        _syncing = true;
        try { _rows.Refresh(); }
        finally { _syncing = false; }

        IsEmpty = _rows.IsEmpty;
        SyncSelection();
    }

    private bool FilterRow(object item)
    {
        if (item is not Strategy s) return false;
        if (!ChipMatches(s)) return false;
        if (_search.Length == 0) return true;

        if (Has(s.DisplayName) || Has(s.Name) || Has(s.Variant) || Has(s.Summary)
            || Has(s.TechnicalSummary) || Has(s.RawCommandLine))
            return true;

        foreach (var tag in s.Tags)
            if (Has(tag))
                return true;

        return false;
    }

    private bool Has(string? source)
        => !string.IsNullOrEmpty(source) && source.Contains(_search, StringComparison.OrdinalIgnoreCase);

    private bool ChipMatches(Strategy s) => _chip switch
    {
        "fav" => s.IsFavorite || s.HasWorked,
        "discord" => HasTag(s, "Discord"),
        "youtube" => HasTag(s, "YouTube") || HasTag(s, "Google"),
        "games" => HasTag(s, "Игры"),
        "alt" => s.Variant.Contains("ALT", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static bool HasTag(Strategy s, string tag)
    {
        foreach (var t in s.Tags)
            if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    // ---------- Выбор ----------

    private void SyncSelection()
    {
        if (_rows is null) return;

        _syncing = true;
        try
        {
            var wanted = _state.SelectedStrategy;
            List.SelectedItem = wanted is not null && _rows.Contains(wanted) ? wanted : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // null прилетает и когда выбранная строка просто отфильтрована — стратегию не сбрасываем.
        if (_syncing) return;
        if (List.SelectedItem is Strategy s)
            _state.SelectedStrategy = s;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: Strategy s }) return;

        _state.SelectedStrategy = s;
        StartIfIdle();
        e.Handled = true;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is Strategy s)
            _state.SelectedStrategy = s;

        if (_state.SelectedStrategy is null)
        {
            _state.Notify("Сначала выберите стратегию", ToastKind.Warning);
            return;
        }

        StartIfIdle();
    }

    private void OnUserListsClick(object sender, RoutedEventArgs e)
        => UserListsEditor.Open();

    // ---------- Избранное и автоподбор ----------

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Strategy s }) return;

        _state.ToggleFavorite(s);
        _state.Notify(s.IsFavorite
            ? $"«{s.DisplayName}» в избранном"
            : $"«{s.DisplayName}» убрана из избранного");

        List.ScrollIntoView(s);
        e.Handled = true;
    }

    private void OnApplyBestClick(object sender, RoutedEventArgs e)
    {
        var best = _state.Tester.Best;
        if (best?.Strategy is null)
        {
            _state.Notify("Перебор ещё не нашёл рабочую стратегию", ToastKind.Warning);
            return;
        }

        _state.SelectedStrategy = best.Strategy;
        List.ScrollIntoView(best.Strategy);
        StartIfIdle();
    }

    private void StartIfIdle()
    {
        if (_state.IsRunning)
        {
            _state.Notify("Обход уже запущен — остановите его, чтобы применить другую стратегию", ToastKind.Warning);
            return;
        }

        if (_state.ToggleBypassCommand.CanExecute(null))
            _state.ToggleBypassCommand.Execute(null);
    }

    // ---------- Модалка «Подробнее» ----------

    private void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Strategy s })
            OpenModal(s);

        e.Handled = true;
    }

    private void OpenModal(Strategy s)
    {
        ModalCard.DataContext = s;
        ModalHost.Visibility = Visibility.Visible;
        PlayModal(open: true);
        ModalCard.Focus();
    }

    private void CloseModal()
    {
        if (ModalHost.Visibility != Visibility.Visible) return;
        PlayModal(open: false);
    }

    private void OnCloseModalClick(object sender, RoutedEventArgs e) => CloseModal();

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        CloseModal();
        e.Handled = true;
    }

    private void OnModalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseModal();
        e.Handled = true;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (ModalCard.DataContext is not Strategy s) return;

        try
        {
            Clipboard.SetText(s.RawCommandLine);
            AppState.Instance.Notify("Аргументы скопированы", ToastKind.Success);
        }
        catch
        {
            // Буфер обмена бывает захвачен другим процессом — это не повод падать.
            AppState.Instance.Notify("Не удалось скопировать аргументы", ToastKind.Warning);
        }
    }

    /// <summary>ModalRaise / ModalDismiss из §7: FillBehavior=Stop, конечные значения — в Completed.</summary>
    private void PlayModal(bool open)
    {
        _modalSb?.Stop(this);
        _modalSb = null;

        bool reduced = Fx.ReducedMotion;

        double scrimTo = open ? 0.85 : 0.0;
        double wrapTo = open ? 1.0 : 0.0;
        double scaleTo = open ? 1.0 : 0.98;
        double slideTo = open ? 0.0 : 10.0;

        if (reduced)
        {
            // Уменьшенная анимация: остаётся только прозрачность, 120 мс Linear.
            ModalScale.ScaleX = 1;
            ModalScale.ScaleY = 1;
            ModalSlide.Y = 0;
            scaleTo = 1;
            slideTo = 0;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(reduced ? 120 : open ? 190 : 130));
        IEasingFunction? ease = reduced
            ? null
            : open
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new QuadraticEase { EasingMode = EasingMode.EaseIn };

        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };
        Add(sb, Scrim, "Opacity", scrimTo, duration, ease);
        Add(sb, ModalWrap, "Opacity", wrapTo, duration, ease);

        if (!reduced)
        {
            Add(sb, ModalScale, "ScaleX", scaleTo, duration, ease);
            Add(sb, ModalScale, "ScaleY", scaleTo, duration, ease);
            Add(sb, ModalSlide, "Y", slideTo, duration, ease);
        }

        // §10: карточка кэшируется только на время раскадровки.
        ModalWrap.CacheMode = new BitmapCache();

        sb.Completed += (_, _) =>
        {
            Scrim.Opacity = scrimTo;
            ModalWrap.Opacity = wrapTo;
            ModalScale.ScaleX = scaleTo;
            ModalScale.ScaleY = scaleTo;
            ModalSlide.Y = slideTo;

            ModalWrap.CacheMode = null;
            _modalSb = null;

            if (!open)
            {
                ModalHost.Visibility = Visibility.Collapsed;
                ModalCard.DataContext = null;
            }
        };

        _modalSb = sb;
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

    // ---------- Подзаголовок ----------

    private void UpdateSubtitle()
    {
        int n = _state.Strategies.Count;
        string word = Plural(n, "профиль", "профиля", "профилей");
        string tail = _state.SelectedStrategy is null
            ? "стратегия не выбрана"
            : "выбран " + _state.SelectedStrategyName;

        Subtitle = $"{n} {word} · {tail}";
    }

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

    /// <summary>
    /// Избранные → та, что у пользователя уже работала → остальные в порядке меню service.bat.
    /// Ставить наверх «рекомендованные» нельзя: у каждого провайдера работает своя стратегия.
    /// </summary>
    private sealed class StrategyOrder : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not Strategy a || y is not Strategy b) return 0;

            int ra = Rank(a);
            int rb = Rank(b);
            if (ra != rb) return ra.CompareTo(rb);

            return a.SortKey.CompareTo(b.SortKey);
        }

        private static int Rank(Strategy s) => s.IsFavorite ? 0 : s.HasWorked ? 1 : 2;
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZapretGui.Core;
using Shapes = System.Windows.Shapes;

namespace ZapretGui.Views;

/// <summary>
/// Страница «Диагностика». Строки списка собираются в коде, а не шаблоном:
/// CheckResult не уведомляет об изменениях, а результат проверки приходит переустановкой
/// элемента коллекции — при биндинге пришлось бы всё равно перестраивать контейнер вручную,
/// зато код полностью владеет раскадровками (крутящаяся дуга, DiagRowReveal) и гасит их.
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private const double RowHeight = 56;
    private const double SweepWidth = 96;

    private readonly AppState _state = AppState.Instance;
    private readonly List<RowVisual> _rows = new();

    private Storyboard? _scan;
    private Storyboard? _sweep;
    private Brush? _sweepMask;
    private bool _attached;
    private bool _isBuildingSupportBundle;
    private bool _isApplyingFix;
    private bool _focusCancelWhenDiagnosticsStarts;

    public DiagnosticsView()
    {
        InitializeComponent();
        _sweepMask = BusySweep.OpacityMask;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        BusyTrack.SizeChanged += OnBusyTrackSizeChanged;
    }

    // ---------- Жизненный цикл ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;

        _state.Diagnostics.CollectionChanged += OnDiagnosticsChanged;
        _state.PropertyChanged += OnStateChanged;

        RebuildAll();
        UpdateBusy();
        UpdateScan();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_attached) return;
        _attached = false;

        _state.Diagnostics.CollectionChanged -= OnDiagnosticsChanged;
        _state.PropertyChanged -= OnStateChanged;

        StopScan();
        StopSweep();
        foreach (var row in _rows) StopSpin(row);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.BusyMessage):
                UpdateBusy();
                break;
            case nameof(AppState.IsDiagnosticsRunning):
                UpdateScan();
                UpdateSummary();
                break;
        }
    }

    // ---------- Коллекция проверок ----------

    private void OnDiagnosticsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (int i = 0; i < e.NewItems.Count; i++)
                    if (e.NewItems[i] is CheckResult added)
                        InsertRow(e.NewStartingIndex + i, added, animate: true);
                break;

            case NotifyCollectionChangedAction.Replace when e.NewItems is not null:
                if (e.NewItems.Count > 0 && e.NewItems[0] is CheckResult replaced)
                    ReplaceRow(e.NewStartingIndex, replaced);
                break;

            case NotifyCollectionChangedAction.Remove:
                RemoveRow(e.OldStartingIndex);
                break;

            default:
                RebuildAll();
                break;
        }

        UpdateSummary();
        UpdateEmptyState();
    }

    private void RebuildAll()
    {
        foreach (var row in _rows) StopSpin(row);
        _rows.Clear();
        RowsHost.Children.Clear();

        for (int i = 0; i < _state.Diagnostics.Count; i++)
            InsertRow(i, _state.Diagnostics[i], animate: false);

        UpdateSummary();
        UpdateEmptyState();
    }

    private void InsertRow(int index, CheckResult result, bool animate)
    {
        if (index < 0 || index > _rows.Count) index = _rows.Count;

        var row = BuildRow(result);
        _rows.Insert(index, row);
        RowsHost.Children.Insert(index, row.Root);

        if (result.Status == CheckStatus.Running) StartSpin(row);
        if (animate) Reveal(row, flash: IsFinished(result.Status));
    }

    private void ReplaceRow(int index, CheckResult result)
    {
        if (index < 0 || index >= _rows.Count)
        {
            RebuildAll();
            return;
        }

        StopSpin(_rows[index]);
        RowsHost.Children.RemoveAt(index);

        var row = BuildRow(result);
        _rows[index] = row;
        RowsHost.Children.Insert(index, row.Root);

        if (result.Status == CheckStatus.Running) StartSpin(row);
        Reveal(row, flash: IsFinished(result.Status));
    }

    private void RemoveRow(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        StopSpin(_rows[index]);
        _rows.RemoveAt(index);
        RowsHost.Children.RemoveAt(index);
    }

    private static bool IsFinished(CheckStatus status)
        => status is CheckStatus.Ok
            or CheckStatus.Warning
            or CheckStatus.Failed
            or CheckStatus.Inconclusive;

    // ---------- Построение строки ----------

    private RowVisual BuildRow(CheckResult result)
    {
        var row = new RowVisual { Result = result };

        row.Root = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(6),
            Background = Res<Brush>("BrushSurfaceRaised"),
            BorderBrush = Res<Brush>("BrushHairlineWeak"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 4),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            RenderTransform = new TranslateTransform(),
        };

        var layer = new Grid();
        row.Root.Child = layer;

        // Вспышка DiagRowReveal — отдельный слой, чтобы не анимировать общую кисть
        row.Flash = new Border
        {
            CornerRadius = new CornerRadius(6),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        row.Flash.SetResourceReference(Border.BackgroundProperty, "BrushAccentWash");
        layer.Children.Add(row.Flash);

        var content = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layer.Children.Add(content);

        // Значок статуса 18×18
        row.GlyphHost = new Grid
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        Grid.SetColumn(row.GlyphHost, 0);
        content.Children.Add(row.GlyphHost);

        if (result.Status == CheckStatus.Running)
        {
            row.Spinner = new Shapes.Path
            {
                Width = 18,
                Height = 18,
                Stretch = Stretch.None,
                Data = Geometry.Parse("M9,2 A7,7 0 1 1 2,9"),
                Fill = null,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
            };
            row.Spinner.SetResourceReference(Shapes.Shape.StrokeProperty, "BrushAccentMid");
            row.GlyphHost.Children.Add(row.Spinner);
        }
        else
        {
            var (icon, brushKey) = GlyphFor(result.Status);
            row.GlyphHost.Children.Add(new Shapes.Path
            {
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                Data = Res<Geometry>(icon),
                Fill = null,
                Stroke = Res<Brush>(brushKey),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        // Название проверки
        var title = new TextBlock
        {
            Style = Res<Style>("CardTitleStyle"),
            Text = result.Title,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        content.Children.Add(title);

        // Результат
        var detail = new TextBlock
        {
            Style = Res<Style>("InlineNumericStyle"),
            Text = result.Detail,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = string.IsNullOrWhiteSpace(result.Detail) ? null : result.Detail,
        };
        if (result.Status == CheckStatus.Failed) detail.Foreground = Res<Brush>("BrushDanger");
        else if (result.Status == CheckStatus.Warning) detail.Foreground = Res<Brush>("BrushWarning");
        else if (result.Status == CheckStatus.Inconclusive) detail.Foreground = Res<Brush>("BrushTextSecondary");
        Grid.SetColumn(detail, 2);
        content.Children.Add(detail);

        // Ссылка на описание проблемы
        if (!string.IsNullOrWhiteSpace(result.Link))
        {
            var link = IconButton("IconExternalStroke", "Открыть описание проблемы");
            link.Tag = row;
            link.Click += OnLinkClick;
            Grid.SetColumn(link, 3);
            content.Children.Add(link);
        }

        // Кнопка исправления
        if (!string.IsNullOrWhiteSpace(result.FixLabel))
        {
            var fix = IconButton("IconWrenchStroke", result.FixLabel!);
            fix.Tag = row;
            fix.Click += OnFixClick;
            fix.IsEnabled = CanApplyFix;
            row.FixButton = fix;
            Grid.SetColumn(fix, 4);
            content.Children.Add(fix);
        }

        return row;
    }

    private Button IconButton(string iconKey, string tip)
    {
        var button = new Button
        {
            Style = Res<Style>("IconButtonStyle"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
            Content = new Shapes.Path
            {
                Style = Res<Style>("IconSmallStyle"),
                Data = Res<Geometry>(iconKey),
            },
        };
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private static (string Icon, string BrushKey) GlyphFor(CheckStatus status) => status switch
    {
        CheckStatus.Ok => ("IconCheckStroke", "BrushSuccess"),
        CheckStatus.Warning => ("IconAlertStroke", "BrushWarning"),
        CheckStatus.Failed => ("IconCloseStroke", "BrushDanger"),
        CheckStatus.Inconclusive => ("IconInfoStroke", "BrushTextTertiary"),
        _ => ("IconInfoStroke", "BrushTextTertiary"),
    };

    // ---------- Действия строки ----------

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RowVisual row } && !string.IsNullOrWhiteSpace(row.Result.Link))
            AppState.OpenExternal(row.Result.Link!);
    }

    private async void OnFixClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not RowVisual row) return;
        if (!CanApplyFix)
            return;

        var result = row.Result;
        if (result.Fix is null)
        {
            _state.Notify("Для этой проверки нет автоматического исправления", ToastKind.Warning);
            return;
        }

        if (RequiresConfirmation(result) && !ConfirmDestructiveFix(result))
            return;

        _isApplyingFix = true;
        UpdateScan();
        try
        {
            var fixResult = await _state.ApplyDiagnosticFixAsync(result);
            var message = string.IsNullOrWhiteSpace(fixResult.Message)
                ? fixResult.Succeeded ? "Исправлено." : "Не удалось применить исправление."
                : fixResult.Message;

            result.Detail = message;
            if (fixResult.Succeeded)
            {
                result.Status = CheckStatus.Ok;
                result.FixLabel = null;
                result.Fix = null;
            }

            var index = _rows.IndexOf(row);
            if (index >= 0) ReplaceRow(index, result);

            UpdateSummary();
            _state.Notify(
                result.Detail,
                fixResult.Succeeded ? ToastKind.Success : ToastKind.Error);
        }
        catch (Exception ex)
        {
            _state.Notify("Не удалось исправить: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            _isApplyingFix = false;
            UpdateScan();
        }
    }

    private bool CanApplyFix =>
        !_state.IsDiagnosticsRunning &&
        !_isApplyingFix &&
        !_isBuildingSupportBundle;

    private static bool RequiresConfirmation(CheckResult result)
        => result.FixLabel?.Contains("Удалить", StringComparison.OrdinalIgnoreCase) == true;

    private bool ConfirmDestructiveFix(CheckResult result)
    {
        var action = result.Id switch
        {
            "windivert_orphan" =>
                "Будет остановлена и удалена служба WinDivert. Если её удерживает другой обход, связанные конфликтующие службы также могут быть удалены.",
            "conflicts" =>
                "Будут остановлены и удалены перечисленные конфликтующие службы обхода, а также связанные службы WinDivert и WinDivert14.",
            _ => "Будет выполнено действие: " + (result.FixLabel ?? "удаление системного компонента") + ".",
        };

        var text = action
                   + "\n\n"
                   + result.Detail
                   + "\n\nПродолжить?";

        var owner = Window.GetWindow(this);
        var answer = owner is null
            ? MessageBox.Show(
                text,
                "Подтвердите удаление",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No)
            : MessageBox.Show(
                owner,
                text,
                "Подтвердите удаление",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    // ---------- Сводка и пустое состояние ----------

    private void UpdateSummary()
    {
        var items = _state.Diagnostics;
        int total = Math.Max(items.Count, DiagnosticsRunner.CheckTitles.Count);
        int ok = 0, warn = 0, failed = 0, inconclusive = 0, running = 0, pending = 0;

        foreach (var item in items)
        {
            switch (item.Status)
            {
                case CheckStatus.Ok: ok++; break;
                case CheckStatus.Warning: warn++; break;
                case CheckStatus.Failed: failed++; break;
                case CheckStatus.Inconclusive: inconclusive++; break;
                case CheckStatus.Running: running++; break;
                case CheckStatus.Pending: pending++; break;
            }
        }

        int completed = ok + warn + failed + inconclusive;
        int missing = Math.Max(0, total - items.Count);
        int withoutResult = inconclusive + running + pending + missing;

        string title, iconKey, brushKey;
        bool notStarted = items.Count == 0 && !_state.IsDiagnosticsRunning;
        if (notStarted)
        {
            title = "Готово к проверке";
            iconKey = "IconInfoStroke";
            brushKey = "BrushTextSecondary";
        }
        else if (_state.IsDiagnosticsRunning || running > 0)
        {
            title = "Идёт проверка";
            iconKey = "IconRefreshStroke";
            brushKey = "BrushAccentMid";
        }
        else if (failed > 0) { title = "Найдены проблемы"; iconKey = "IconCloseStroke"; brushKey = "BrushDanger"; }
        else if (warn > 0) { title = "Есть предупреждения"; iconKey = "IconAlertStroke"; brushKey = "BrushWarning"; }
        else if (withoutResult > 0)
        {
            title = "Не удалось проверить";
            iconKey = "IconInfoStroke";
            brushKey = "BrushTextSecondary";
        }
        else { title = "Всё в порядке"; iconKey = "IconCheckStroke"; brushKey = "BrushSuccess"; }

        var brush = Res<Brush>(brushKey);
        var countText = notStarted
            ? $"{total} проверок ожидают запуска"
            : _state.IsDiagnosticsRunning || running > 0
                ? $"{completed} из {total} проверок завершено"
                : withoutResult > 0
                    ? $"{ok} из {total} проверок пройдено · {withoutResult} без результата"
                    : $"{ok} из {total} проверок пройдено";
        bool announce = SummaryTitle.Text != title || SummaryCount.Text != countText;
        SummaryTitle.Text = title;
        SummaryCount.Text = countText;
        AutomationProperties.SetName(
            SummaryTitle,
            title + ". " + countText);
        SummaryGlyph.Data = Res<Geometry>(iconKey);
        SummaryGlyph.Stroke = brush;
        SummaryRing.Stroke = brush;
        SummaryProgress.Width = total > 0
            ? Math.Round(200.0 * (_state.IsDiagnosticsRunning ? completed : ok) / total)
            : 0;

        if (announce && SummaryTitle.IsLoaded)
            RaiseLiveRegion(SummaryTitle);
    }

    private void UpdateEmptyState()
    {
        bool empty = _state.Diagnostics.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RowsBox.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        SummaryCard.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Раскладка ----------

    /// <summary>
    /// Тело страницы всегда занимает всю высоту области прокрутки: иначе список проверок
    /// сжимается к верху, а под ним остаётся пустота до панели обслуживания.
    /// </summary>
    private void OnPageScrollSizeChanged(object sender, SizeChangedEventArgs e)
        => PageBody.MinHeight = Math.Max(0, Math.Floor(e.NewSize.Height) - 1);

    // ---------- Движение ----------

    /// <summary>DiagRowReveal: базовые значения уже конечные, анимация идёт From→To и снимается.</summary>
    private void Reveal(RowVisual row, bool flash)
    {
        var story = new Storyboard();
        bool calm = ReducedMotion;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(calm ? 120 : 180))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = calm ? null : Res<IEasingFunction>("EaseOutCubic"),
        };
        Storyboard.SetTarget(fade, row.Root);
        Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
        story.Children.Add(fade);

        if (!calm)
        {
            var slide = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180))
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = Res<IEasingFunction>("EaseOutCubic"),
            };
            Storyboard.SetTarget(slide, row.Root);
            Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            story.Children.Add(slide);

            foreach (var axis in new[] { "ScaleX", "ScaleY" })
            {
                var pop = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(180))
                {
                    FillBehavior = FillBehavior.Stop,
                    EasingFunction = Res<IEasingFunction>("EaseOutCubic"),
                };
                Storyboard.SetTarget(pop, row.GlyphHost);
                Storyboard.SetTargetProperty(pop, new PropertyPath($"(UIElement.RenderTransform).(ScaleTransform.{axis})"));
                story.Children.Add(pop);
            }

            if (flash)
            {
                var wash = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)) { FillBehavior = FillBehavior.Stop };
                Storyboard.SetTarget(wash, row.Flash);
                Storyboard.SetTargetProperty(wash, new PropertyPath("Opacity"));
                story.Children.Add(wash);
            }
        }

        story.Completed += (_, _) =>
        {
            row.Root.Opacity = 1;
            row.Flash.Opacity = 0;
            if (row.Root.RenderTransform is TranslateTransform shift) shift.Y = 0;
            if (row.GlyphHost.RenderTransform is ScaleTransform scale) { scale.ScaleX = 1; scale.ScaleY = 1; }
        };
        story.Begin(this);
    }

    private void StartSpin(RowVisual row)
    {
        if (row.Spinner is null || ReducedMotion) return;
        StopSpin(row);

        var turn = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(turn, row.Spinner);
        Storyboard.SetTargetProperty(turn, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));

        row.Spin = new Storyboard();
        row.Spin.Children.Add(turn);
        row.Spin.Begin(this, true);
    }

    private void StopSpin(RowVisual row)
    {
        if (row.Spin is null) return;
        row.Spin.Stop(this);
        row.Spin = null;
    }

    private void UpdateScan()
    {
        bool running = _state.IsDiagnosticsRunning;
        bool moveFocusToCancel = running &&
                                 (_focusCancelWhenDiagnosticsStarts ||
                                  RunAllButton.IsKeyboardFocusWithin ||
                                  EmptyRunAllButton.IsKeyboardFocusWithin);
        bool moveFocusToRun = !running && CancelDiagnosticsButton.IsKeyboardFocusWithin;

        RunAllButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CancelDiagnosticsButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        bool canStart = !running &&
                        !_isApplyingFix &&
                        !_isBuildingSupportBundle;
        RunAllButton.IsEnabled = canStart;
        EmptyRunAllButton.IsEnabled = canStart;
        SupportBundleButton.IsEnabled =
            !running &&
            !_isApplyingFix &&
            !_isBuildingSupportBundle;

        foreach (var row in _rows)
            if (row.FixButton is not null)
                row.FixButton.IsEnabled = CanApplyFix;

        if (moveFocusToCancel)
        {
            _focusCancelWhenDiagnosticsStarts = false;
            CancelDiagnosticsButton.Focus();
        }
        else if (moveFocusToRun)
            RunAllButton.Focus();

        if (running && !ReducedMotion) StartScan();
        else StopScan();
    }

    private void OnRunDiagnosticsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button button && button.IsKeyboardFocusWithin)
            _focusCancelWhenDiagnosticsStarts = true;
    }

    // ---------- Отчёт для поддержки ----------

    private async void OnSupportBundleClick(object sender, RoutedEventArgs e)
    {
        if (_isBuildingSupportBundle ||
            _state.IsDiagnosticsRunning ||
            _isApplyingFix)
        {
            _state.Notify(
                "Дождитесь завершения диагностики или исправления, затем соберите отчёт.",
                ToastKind.Info);
            return;
        }

        bool restoreKeyboardFocus =
            SupportBundleButton.IsKeyboardFocusWithin;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить отчёт для поддержки",
            Filter = "ZIP-архив (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "zapret-support-"
                       + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                       + ".zip",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        _isBuildingSupportBundle = true;
        SupportBundleButton.Content = "Собираем…";
        SupportBundleStatus.Visibility = Visibility.Collapsed;
        UpdateScan();

        try
        {
            var result = await SupportBundleService.CreateAsync(dialog.FileName, _state);
            SupportBundleStatus.Foreground = Res<Brush>("BrushSuccess");
            SupportBundleStatus.Text =
                $"Готово: {Path.GetFileName(dialog.FileName)} · "
                + $"{result.DiagnosticCount} проверок, {result.LogLineCount} строк журнала";
            SupportBundleStatus.Visibility = Visibility.Visible;
            RaiseLiveRegion(SupportBundleStatus);
            _state.Notify(
                "Обезличенный отчёт сохранён — содержимое пользовательских списков не включено",
                ToastKind.Success);
        }
        catch (Exception ex)
        {
            SupportBundleStatus.Foreground = Res<Brush>("BrushDanger");
            SupportBundleStatus.Text = "Не удалось собрать отчёт. Попробуйте выбрать другую папку.";
            SupportBundleStatus.Visibility = Visibility.Visible;
            RaiseLiveRegion(SupportBundleStatus);
            _state.Notify("Не удалось собрать отчёт: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            _isBuildingSupportBundle = false;
            SupportBundleButton.Content = "Собрать отчёт";
            UpdateScan();
            if (restoreKeyboardFocus)
                SupportBundleButton.Focus();
        }
    }

    private static void RaiseLiveRegion(UIElement element)
    {
        var peer = UIElementAutomationPeer.FromElement(element)
                   ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void StartScan()
    {
        if (_scan is not null) return;

        double travel = Math.Max(0, (SummaryCard.ActualHeight > 0 ? SummaryCard.ActualHeight : 88) - 2);
        var run = new DoubleAnimation(0, travel, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(run, ScanBar);
        Storyboard.SetTargetProperty(run, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        ScanBar.Opacity = 1;
        _scan = new Storyboard();
        _scan.Children.Add(run);
        _scan.Begin(this, true);
    }

    private void StopScan()
    {
        if (_scan is not null)
        {
            _scan.Stop(this);
            _scan = null;
        }
        ScanBar.Opacity = 0;
        if (ScanBar.RenderTransform is TranslateTransform shift) shift.Y = 0;
    }

    // ---------- Полоса длительной операции ----------

    private void UpdateBusy()
    {
        var message = _state.BusyMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            StopSweep();
            BusyBlock.Visibility = Visibility.Collapsed;
            return;
        }

        BusyText.Text = message;
        BusyBlock.Visibility = Visibility.Visible;
        StartSweep();
    }

    private void OnBusyTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BusyBlock.Visibility == Visibility.Visible) StartSweep();
    }

    /// <summary>Indeterminate по §7: блик 96 DIP идёт −96 → ширина трека. В «спокойном» режиме — статичная заливка 30 %.</summary>
    private void StartSweep()
    {
        StopSweep();

        // Ширина карточки теперь зависит от окна; запасное значение нужно только
        // до первой раскладки, дальше подхватывается через OnBusyTrackSizeChanged.
        double track = BusyTrack.ActualWidth > 0 ? BusyTrack.ActualWidth : 640;

        if (ReducedMotion)
        {
            BusySweep.OpacityMask = null;
            BusySweep.Width = Math.Round(track * 0.3);
            if (BusySweep.RenderTransform is TranslateTransform still) still.X = 0;
            return;
        }

        BusySweep.OpacityMask = _sweepMask;
        BusySweep.Width = SweepWidth;
        var glide = new DoubleAnimation(-SweepWidth, track, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(glide, BusySweep);
        Storyboard.SetTargetProperty(glide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

        _sweep = new Storyboard();
        _sweep.Children.Add(glide);
        _sweep.Begin(this, true);
    }

    private void StopSweep()
    {
        if (_sweep is null) return;
        _sweep.Stop(this);
        _sweep = null;
        if (BusySweep.RenderTransform is TranslateTransform shift) shift.X = -SweepWidth;
    }

    // ---------- Мелочи ----------

    /// <summary>
    /// Правило последней строки §7. Отдельного класса Fx здесь намеренно не трогаем:
    /// условия форсирования взяты из спецификации напрямую, чтобы страница не зависела
    /// от порядка появления файлов в проекте.
    /// </summary>
    private static bool ReducedMotion =>
        AppSettings.Current.ReducedMotion
        || !SystemParameters.ClientAreaAnimation
        || (RenderCapability.Tier >> 16) < 2;

    private T Res<T>(string key) => (T)FindResource(key);

    private sealed class RowVisual
    {
        public CheckResult Result = null!;
        public Border Root = null!;
        public Border Flash = null!;
        public Grid GlyphHost = null!;
        public Shapes.Path? Spinner;
        public Storyboard? Spin;
        public Button? FixButton;
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZapretGui.Core;

namespace ZapretGui.Views;

public sealed class UserListEntry
{
    public UserListEntry(string value) => Value = value;
    public string Value { get; set; }
}

public partial class UserListsEditor : System.Windows.Controls.UserControl
{
    private readonly Dictionary<UserListKind, ObservableCollection<UserListEntry>> _entries = new()
    {
        [UserListKind.BypassDomains] = new(),
        [UserListKind.ExcludedDomains] = new(),
        [UserListKind.ExcludedIps] = new(),
    };

    private readonly Dictionary<UserListKind, List<string>> _savedEntries = new()
    {
        [UserListKind.BypassDomains] = new(),
        [UserListKind.ExcludedDomains] = new(),
        [UserListKind.ExcludedIps] = new(),
    };

    private readonly Dictionary<UserListKind, string> _draftEntries = new()
    {
        [UserListKind.BypassDomains] = string.Empty,
        [UserListKind.ExcludedDomains] = string.Empty,
        [UserListKind.ExcludedIps] = string.Empty,
    };

    private UserListKind _activeKind = UserListKind.BypassDomains;
    private bool _loading;

    public UserListsEditor()
    {
        InitializeComponent();
    }

    public void Open()
    {
        UserListsSnapshot snapshot;
        try
        {
            AppPaths.EnsureUserLists();
            snapshot = UserListManager.Load();
        }
        catch (Exception ex)
        {
            AppState.Instance.Notify(
                "Не удалось прочитать пользовательские списки: " + ex.Message,
                ToastKind.Error);
            return;
        }

        _loading = true;
        try
        {
            Replace(UserListKind.BypassDomains, snapshot.BypassDomains);
            Replace(UserListKind.ExcludedDomains, snapshot.ExcludedDomains);
            Replace(UserListKind.ExcludedIps, snapshot.ExcludedIps);
            foreach (UserListKind kind in _draftEntries.Keys.ToArray())
                _draftEntries[kind] = string.Empty;

            _activeKind = UserListKind.BypassDomains;
            BypassTab.IsChecked = true;
            SetActiveKind();

            HideNotice();
            CaptureSavedState();
        }
        finally
        {
            _loading = false;
        }

        UpdateDirtyState();
        Visibility = Visibility.Visible;
        Panel.SetZIndex(this, 100);

        ModalCard.Focus();
        NewEntries.Focus();
    }

    public bool ConfirmDiscardForApplicationExit() =>
        !HasUnsavedChanges() || ConfirmDiscardChanges();

    private void Close()
    {
        Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
    }

    private void RequestClose()
    {
        if (HasUnsavedChanges())
        {
            if (!ConfirmDiscardChanges())
                return;
            DiscardWorkingChanges();
        }

        Close();
    }

    private void DiscardWorkingChanges()
    {
        _loading = true;
        try
        {
            foreach (UserListKind kind in _entries.Keys)
            {
                Replace(kind, _savedEntries[kind]);
                _draftEntries[kind] = string.Empty;
            }
            SetActiveKind();
        }
        finally
        {
            _loading = false;
        }

        UpdateDirtyState();
    }

    private void Replace(UserListKind kind, IEnumerable<string> values)
    {
        ObservableCollection<UserListEntry> target = _entries[kind];
        target.Clear();
        foreach (string value in values)
            target.Add(new UserListEntry(value));
    }

    private void OnKindChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse(tag, out UserListKind kind))
            return;

        if (!_loading && NewEntries is not null)
            _draftEntries[_activeKind] = NewEntries.Text;
        _activeKind = kind;
        if (EntriesList is not null)
            SetActiveKind();
    }

    private void SetActiveKind()
    {
        EntriesList.ItemsSource = _entries[_activeKind];

        switch (_activeKind)
        {
            case UserListKind.BypassDomains:
                KindDescription.Text =
                    "Домены, для которых нужно применять обход. Можно вставить адрес сайта целиком — путь будет убран автоматически.";
                NewEntries.Tag = "example.com или https://example.com/page";
                break;

            case UserListKind.ExcludedDomains:
                KindDescription.Text =
                    "Домены, которые обход не должен затрагивать. Полезно, если обычный сайт перестал открываться при включённом winws.";
                NewEntries.Tag = "example.com";
                break;

            case UserListKind.ExcludedIps:
                KindDescription.Text =
                    "IP-адреса и подсети, которые обход не должен затрагивать. Поддерживаются IPv4, IPv6 и CIDR.";
                NewEntries.Tag = "192.0.2.10 или 192.0.2.0/24";
                break;
        }

        NewEntries.Text = _draftEntries[_activeKind];
        HideNotice();
        UpdateEmptyState();
        UpdateDirtyState();
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => AddInput();

    private void OnNewEntriesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        AddInput();
        e.Handled = true;
    }

    private bool AddInput()
    {
        _draftEntries[_activeKind] = NewEntries.Text;
        if (!TryCommitDraft(_activeKind))
            return false;

        NewEntries.Text = string.Empty;
        HideNotice();
        UpdateEmptyState();
        UpdateDirtyState();
        NewEntries.Focus();
        return true;
    }

    private bool TryCommitDraft(UserListKind kind)
    {
        string draft = _draftEntries[kind];
        IReadOnlyList<string> rawValues = UserListManager.SplitInput(draft);
        if (rawValues.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(draft))
            {
                _draftEntries[kind] = string.Empty;
                return true;
            }

            ActivateKind(kind);
            ShowError(kind == UserListKind.ExcludedIps
                ? "Введите IP-адрес или подсеть."
                : "Введите домен или URL.");
            return false;
        }

        ObservableCollection<UserListEntry> target = _entries[kind];
        var existing = new HashSet<string>(
            target.Select(static entry => entry.Value),
            StringComparer.OrdinalIgnoreCase);

        var normalizedValues = new List<string>();
        foreach (string raw in rawValues)
        {
            if (!UserListManager.TryNormalize(kind, raw, out string normalized, out string error))
            {
                ActivateKind(kind);
                ShowError($"«{raw}»: {error}.");
                return false;
            }

            if (existing.Add(normalized))
                normalizedValues.Add(normalized);
        }

        foreach (string value in normalizedValues)
            target.Add(new UserListEntry(value));

        _draftEntries[kind] = string.Empty;
        if (kind == _activeKind &&
            string.Equals(NewEntries.Text, draft, StringComparison.Ordinal))
            NewEntries.Text = string.Empty;
        return true;
    }

    private void ActivateKind(UserListKind kind)
    {
        RadioButton tab = kind switch
        {
            UserListKind.ExcludedDomains => ExcludedDomainsTab,
            UserListKind.ExcludedIps => ExcludedIpsTab,
            _ => BypassTab,
        };

        tab.IsChecked = true;
        if (_activeKind != kind)
            _activeKind = kind;
        SetActiveKind();
        NewEntries.Focus();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UserListEntry entry })
            _entries[_activeKind].Remove(entry);

        HideNotice();
        UpdateEmptyState();
        UpdateDirtyState();
        e.Handled = true;
    }

    private void OnEntryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;

        // UpdateSourceTrigger=PropertyChanged обновляет Value в той же волне событий.
        // Откладываем сравнение до DataBind, чтобы оно всегда видело новое значение.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, UpdateDirtyState);
    }

    private void OnNewEntriesTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;

        _draftEntries[_activeKind] = NewEntries.Text;
        UpdateDirtyState();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _draftEntries[_activeKind] = NewEntries.Text;
        foreach (UserListKind kind in _draftEntries.Keys.ToArray())
        {
            if (!TryCommitDraft(kind))
            {
                UpdateDirtyState();
                return;
            }
        }
        NewEntries.Text = string.Empty;

        var snapshot = new UserListsSnapshot
        {
            BypassDomains = Values(UserListKind.BypassDomains),
            ExcludedDomains = Values(UserListKind.ExcludedDomains),
            ExcludedIps = Values(UserListKind.ExcludedIps),
        };

        try
        {
            UserListManager.Save(snapshot);
        }
        catch (Exception ex)
        {
            ShowError("Не удалось сохранить: " + ex.Message);
            return;
        }

        CaptureSavedState();
        UpdateDirtyState();

        var state = AppState.Instance;
        state.Notify("Пользовательские списки сохранены", ToastKind.Success);

        if (state.ServiceState == ServiceState.Running
            && BypassController.Instance.ActiveStrategy is null)
        {
            ShowInfo("Списки сохранены. Служба прочитает их после следующего запуска.", canRestart: false);
            return;
        }

        if (state.IsRunning)
        {
            ShowInfo("Списки сохранены. Чтобы применить изменения, перезапустите обход.", canRestart: true);
            return;
        }

        Close();
    }

    private List<string> Values(UserListKind kind) =>
        _entries[kind]
            .Select(static entry => entry.Value)
            .ToList();

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        var command = state.ToggleBypassCommand;

        RestartButton.IsEnabled = false;
        NoticeText.Text = "Перезапускаю обход…";

        try
        {
            if (state.IsRunning)
            {
                if (!command.CanExecute(null))
                {
                    ShowError("Сейчас обход занят другой операцией. Попробуйте ещё раз.");
                    return;
                }

                command.Execute(null);

                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while ((state.IsRunning || command.IsRunning) && DateTime.UtcNow < deadline)
                    await Task.Delay(120);

                if (state.IsRunning)
                {
                    ShowError("Не удалось остановить обход за 15 секунд.");
                    return;
                }
            }

            if (!command.CanExecute(null))
            {
                ShowError("Не удалось запустить выбранную стратегию.");
                return;
            }

            command.Execute(null);
            Close();
        }
        finally
        {
            RestartButton.IsEnabled = true;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Lists);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.Lists,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Не удалось открыть папку: " + ex.Message);
        }
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        RequestClose();
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => RequestClose();

    private void OnModalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        RequestClose();
        e.Handled = true;
    }

    private void CaptureSavedState()
    {
        foreach (UserListKind kind in _entries.Keys)
        {
            List<string> target = _savedEntries[kind];
            target.Clear();
            target.AddRange(Values(kind));
        }
    }

    private bool HasUnsavedChanges()
    {
        if (_draftEntries.Values.Any(
                static draft => !string.IsNullOrWhiteSpace(draft)))
            return true;

        foreach (UserListKind kind in _entries.Keys)
        {
            if (!Values(kind).SequenceEqual(_savedEntries[kind], StringComparer.Ordinal))
                return true;
        }

        return false;
    }

    private void UpdateDirtyState()
    {
        if (_loading || DirtyIndicator is null || SaveButton is null)
            return;

        bool dirty = HasUnsavedChanges();
        DirtyIndicator.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = dirty;
    }

    private bool ConfirmDiscardChanges()
    {
        const string question =
            "Закрыть редактор без сохранения?\n\n" +
            "Добавленные, удалённые и изменённые значения будут потеряны.";

        Window? owner = Window.GetWindow(this);
        MessageBoxResult answer = owner is null
            ? MessageBox.Show(
                question,
                "Несохранённые изменения",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No)
            : MessageBox.Show(
                owner,
                question,
                "Несохранённые изменения",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _entries[_activeKind].Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowError(string text)
    {
        NoticeText.Text = text;
        NoticeText.Foreground = FindResource("BrushDanger") as Brush
                                ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x6D));
        RestartButton.Visibility = Visibility.Collapsed;
        Notice.Visibility = Visibility.Visible;
    }

    private void ShowInfo(string text, bool canRestart)
    {
        NoticeText.Text = text;
        NoticeText.Foreground = FindResource("BrushTextSecondary") as Brush
                                ?? new SolidColorBrush(Color.FromRgb(0xA9, 0xB4, 0xC0));
        RestartButton.Visibility = canRestart ? Visibility.Visible : Visibility.Collapsed;
        Notice.Visibility = Visibility.Visible;
    }

    private void HideNotice()
    {
        if (Notice is null)
            return;

        Notice.Visibility = Visibility.Collapsed;
        RestartButton.Visibility = Visibility.Collapsed;
    }
}

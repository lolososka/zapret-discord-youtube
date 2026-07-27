using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZapretGui.Core;

namespace ZapretGui.Views;

/// <summary>
/// Страница «Телеграм». Работает с <see cref="TelegramProxy"/> напрямую, без биндингов:
/// DataContext страниц занят AppState, а состояние здесь меняется извне (утилита живёт
/// в трее и может быть остановлена мимо оболочки), поэтому всё синхронизируется вручную.
/// </summary>
public partial class TelegramView : UserControl
{
    private readonly TelegramProxy _proxy = TelegramProxy.Instance;
    private readonly AppState _state = AppState.Instance;

    private bool _suppress;
    private bool _attached;
    private bool _busy;

    public TelegramView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ---------- Жизненный цикл ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_attached)
        {
            _attached = true;
            _proxy.StateChanged += OnProxyStateChanged;
        }

        _suppress = true;
        try
        {
            SecretBox.Text = _proxy.Secret ?? string.Empty;
            AutoStartSwitch.IsChecked = _proxy.AutoStartWithBypass;
            HostBox.Text = _proxy.Host;
            PortBox.Text = _proxy.PortText;
            SuggestedFolderText.Text = TelegramProxy.SuggestedFolder;
            SuggestedFolderText.ToolTip = TelegramProxy.SuggestedFolder;
        }
        finally
        {
            _suppress = false;
        }

        Sync();

        // перебор процессов не на потоке интерфейса — Refresh сам вернёт уведомления в UI
        await Task.Run(_proxy.Refresh);
        Sync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_attached)
            return;
        _attached = false;
        _proxy.StateChanged -= OnProxyStateChanged;
    }

    private void OnProxyStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            Sync();
            return;
        }

        try { Dispatcher.BeginInvoke(new Action(Sync)); }
        catch { /* окно уже закрывается */ }
    }

    // ---------- Отрисовка состояния ----------

    private void Sync()
    {
        var state = _proxy.State;

        StateDot.Fill = FindBrush(state switch
        {
            TgProxyState.Running => "BrushStateRunning",
            TgProxyState.Stopped => "BrushStateStopped",
            _ => "BrushTextDisabled"
        });

        StateTitleText.Text = _proxy.StateTitle;

        var found = _proxy.IsFound;
        PathText.Text = _proxy.ExecutablePathText;
        PathText.ToolTip = found ? _proxy.ExecutablePath : null;

        FoundPanel.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        MissingPanel.Visibility = found ? Visibility.Collapsed : Visibility.Visible;

        var running = _proxy.IsRunning;
        PidPill.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PidText.Text = "PID " + _proxy.ProcessIdText;

        StartButton.IsEnabled = !_busy && _proxy.CanStart;
        StopButton.IsEnabled = !_busy && _proxy.CanStop;
        PickButton.IsEnabled = !_busy;
        PickButtonMissing.IsEnabled = !_busy;
        RefreshButton.IsEnabled = !_busy;

        SyncConfigureButton();
    }

    private void SyncConfigureButton()
        => ConfigureButton.IsEnabled = SecretBox.Text.Trim().Length > 0;

    private Brush FindBrush(string key)
    {
        if (TryFindResource(key) is Brush brush)
            return brush;
        return Brushes.Transparent;
    }

    // ---------- Запуск и остановка ----------

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await Task.Run(_proxy.Refresh);
        Sync();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        SetBusy(true, "Запускается…");
        bool ok;
        try
        {
            ok = await _proxy.StartAsync();
        }
        finally
        {
            SetBusy(false, "Запустить");
        }

        if (ok)
        {
            _state.Notify("TgWsProxy запущен, слушает 127.0.0.1:1443", ToastKind.Success);
            return;
        }

        _state.Notify("Не удалось запустить TgWsProxy — проверьте файл и права", ToastKind.Error);
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        _busy = true;
        StopButton.Content = "Останавливается…";
        Sync();
        try
        {
            await _proxy.StopAsync();
        }
        finally
        {
            _busy = false;
            StopButton.Content = "Остановить";
            Sync();
        }

        if (_proxy.IsRunning)
        {
            _state.Notify("TgWsProxy не удалось завершить", ToastKind.Error);
            return;
        }

        _state.Notify("TgWsProxy остановлен", ToastKind.Info);
    }

    private void SetBusy(bool busy, string startCaption)
    {
        _busy = busy;
        StartButton.Content = startCaption;
        Sync();
    }

    // ---------- Файл ----------

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите TgWsProxy",
            Filter = "TgWsProxy (TgWsProxy*.exe)|TgWsProxy*.exe|Программы (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = TelegramProxy.SuggestedFolder
        };

        bool? answer;
        try
        {
            var owner = Window.GetWindow(this);
            answer = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        }
        catch
        {
            answer = false;
        }

        if (answer != true)
            return;

        if (_proxy.SetExecutablePath(dialog.FileName))
        {
            Sync();
            _state.Notify("Файл TgWsProxy указан", ToastKind.Success);
            return;
        }

        _state.Notify("Этот файл не подходит — нужен exe утилиты TgWsProxy", ToastKind.Warning);
    }

    private void OnFolderClick(object sender, RoutedEventArgs e) => _proxy.OpenFolder();

    private void OnDownloadClick(object sender, RoutedEventArgs e) => _proxy.OpenReleasesPage();

    // ---------- Адрес и секрет ----------

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        var (text, title) = tag switch
        {
            "port" => (_proxy.PortText, "Порт скопирован"),
            _ => (_proxy.Host, "Адрес скопирован")
        };

        if (CopyToClipboard(text))
        {
            _state.Notify(title, ToastKind.Success);
            return;
        }

        _state.Notify("Буфер обмена занят другим приложением", ToastKind.Warning);
    }

    private static bool CopyToClipboard(string text)
    {
        // буфер захватывает то одно, то другое приложение — одна повторная попытка
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch
            {
                // следующая попытка
            }
        }
        return false;
    }

    private void OnSecretTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress)
            return;
        SyncConfigureButton();
    }

    private void OnSecretLostFocus(object sender, RoutedEventArgs e) => CommitSecret();

    private void OnSecretKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitSecret();
        e.Handled = true;
    }

    /// <summary>Пишет секрет в настройки и подставляет очищенное значение обратно в поле.</summary>
    private void CommitSecret()
    {
        if (_suppress)
            return;

        _proxy.Secret = SecretBox.Text;

        var stored = _proxy.Secret ?? string.Empty;
        if (!string.Equals(SecretBox.Text, stored, StringComparison.Ordinal))
        {
            _suppress = true;
            try
            {
                SecretBox.Text = stored;
                SecretBox.CaretIndex = stored.Length;
            }
            finally
            {
                _suppress = false;
            }
        }

        SyncConfigureButton();
    }

    private void OnConfigureClick(object sender, RoutedEventArgs e)
    {
        CommitSecret();

        if (!_proxy.HasSecret)
        {
            _state.Notify("Сначала вставьте секрет из окна TgWsProxy", ToastKind.Warning);
            return;
        }

        _proxy.OpenTelegramLink();
    }

    // ---------- Запуск вместе с обходом ----------

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;
        _proxy.AutoStartWithBypass = AutoStartSwitch.IsChecked == true;
    }
}

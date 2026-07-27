using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ZapretGui.Controls;
using ZapretGui.Core;
using ZapretGui.Themes;

namespace ZapretGui.Views;

/// <summary>
/// Страница «Настройки». Значения читаются один раз при создании под флагом <see cref="_suppress"/>:
/// без него Checked/Unchecked при инициализации переписали бы файл настроек и дёрнули реестр.
/// Каждая запись завершается AppSettings.Save() — приложение может быть закрыто в трей и убито.
/// </summary>
public partial class SettingsView : UserControl
{
    private const string DiscordFakeFile = "ACTIVE_DISCORD_UDP.bin";
    private const string GameFakeFile = "ACTIVE_GAME_UDP.bin";

    private readonly AppState _state = AppState.Instance;

    private bool _suppress;
    private bool _fakesLoaded;
    private bool _attached;

    public SettingsView()
    {
        InitializeComponent();

        _suppress = true;
        try { LoadValues(); }
        finally { _suppress = false; }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ---------- Жизненный цикл ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_attached)
        {
            _attached = true;
            _state.PropertyChanged += OnStateChanged;
        }

        if (_fakesLoaded) return;
        _fakesLoaded = true;
        await LoadFakesAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_attached) return;
        _attached = false;
        _state.PropertyChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.GameFilter):
                Quiet(() => Select(GameFilterSegments, _state.GameFilter.ToString()));
                break;
            case nameof(AppState.IpsetMode):
                Quiet(() => Select(IpsetSegments, _state.IpsetMode.ToString()));
                break;
            case nameof(AppState.AutoUpdateCheck):
                Quiet(() => AutoUpdateSwitch.IsChecked = _state.AutoUpdateCheck);
                break;
        }
    }

    // ---------- Чтение значений ----------

    private void LoadValues()
    {
        var s = AppSettings.Current;

        // Ресурсы приложения могли остаться с акцентом по умолчанию — приводим их к настройке.
        AccentManager.Apply(s.Accent);
        Select(AccentPicker, AccentManager.CurrentName);

        ReducedMotionSwitch.IsChecked = s.ReducedMotion;

        // Источник истины по автозапуску — реестр, а не файл настроек.
        var autostart = Autostart.IsEnabled();
        s.StartWithWindows = autostart;
        StartWithWindowsSwitch.IsChecked = autostart;

        StartMinimizedSwitch.IsChecked = s.StartMinimized;
        AutoStartBypassSwitch.IsChecked = s.AutoStartBypass;
        MinimizeToTraySwitch.IsChecked = s.MinimizeToTray;
        CloseToTraySwitch.IsChecked = s.CloseToTray;
        CheckUpdatesOnLaunchSwitch.IsChecked = s.CheckUpdatesOnLaunch;
        AutoUpdateSwitch.IsChecked = _state.AutoUpdateCheck;

        Select(GameFilterSegments, _state.GameFilter.ToString());
        Select(IpsetSegments, _state.IpsetMode.ToString());   // Unknown — ни один чип не выбран

        RootPathText.Text = AppPaths.Root;
        RootPathText.ToolTip = AppPaths.Root;
        ReleasesLink.Tag = UpdateService.DownloadUrl;
    }

    private async Task LoadFakesAsync()
    {
        List<string> files;
        string discord, game;

        try
        {
            // CurrentActiveFake считает SHA-256 всех bin\*.bin — не на UI-потоке
            var probe = await Task.Run(() => (
                Files: FeatureFlags.ListFakeFiles(),
                Discord: FeatureFlags.CurrentActiveFake(DiscordFakeFile),
                Game: FeatureFlags.CurrentActiveFake(GameFakeFile)));

            files = probe.Files;
            discord = probe.Discord;
            game = probe.Game;
        }
        catch
        {
            return;
        }

        Quiet(() =>
        {
            FillCombo(DiscordFakeCombo, files, discord);
            FillCombo(GameFakeCombo, files, game);
        });
    }

    private static void FillCombo(ComboBox combo, List<string> items, string current)
    {
        combo.ItemsSource = new List<string>(items);

        if (items.Count == 0)
        {
            combo.IsEnabled = false;
            combo.ToolTip = "В папке bin нет файлов *.bin";
            return;
        }

        combo.SelectedItem = items.FirstOrDefault(i => string.Equals(i, current, StringComparison.OrdinalIgnoreCase));
        combo.ToolTip = combo.SelectedItem is null
            ? "Активный файл не совпадает ни с одним фейком из папки bin"
            : null;
    }

    // ---------- Внешний вид ----------

    private void OnAccentChecked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not RadioButton { Tag: string name }) return;

        AccentManager.Apply(name);
        AppSettings.Current.Accent = AccentManager.CurrentName;
        AppSettings.Save();
    }

    private void OnReducedMotionToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;

        var on = ReducedMotionSwitch.IsChecked == true;
        AppSettings.Current.ReducedMotion = on;
        AppSettings.Save();
        Fx.ReducedMotion = on;
    }

    // ---------- Запуск ----------

    private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;

        var on = StartWithWindowsSwitch.IsChecked == true;
        Autostart.Set(on);

        // Политика домена может запретить запись в Run — показываем фактическое состояние
        var actual = Autostart.IsEnabled();
        AppSettings.Current.StartWithWindows = actual;
        AppSettings.Save();

        if (actual == on) return;

        Quiet(() => StartWithWindowsSwitch.IsChecked = actual);
        _state.Notify("Не удалось изменить автозапуск — запись в реестр запрещена", ToastKind.Error);
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        AppSettings.Current.StartMinimized = StartMinimizedSwitch.IsChecked == true;
        AppSettings.Save();
    }

    private void OnAutoStartBypassToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        AppSettings.Current.AutoStartBypass = AutoStartBypassSwitch.IsChecked == true;
        AppSettings.Save();
    }

    // ---------- Окно ----------

    private void OnMinimizeToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        AppSettings.Current.MinimizeToTray = MinimizeToTraySwitch.IsChecked == true;
        AppSettings.Save();
    }

    private void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        AppSettings.Current.CloseToTray = CloseToTraySwitch.IsChecked == true;
        AppSettings.Save();
    }

    // ---------- Трафик ----------

    private void OnGameFilterChecked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<GameFilterMode>(tag, out var mode)) return;

        _state.GameFilter = mode;
        AppSettings.Save();
    }

    private async void OnIpsetChecked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<IpsetMode>(tag, out var mode)) return;

        IpsetSegments.IsEnabled = false;
        try
        {
            await _state.SetIpsetModeAsync(mode);
        }
        catch (Exception ex)
        {
            _state.Notify("Не удалось переключить IPSet: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            IpsetSegments.IsEnabled = true;
        }

        // Восстановить список нечем, если ipset-all.txt.backup отсутствует — показываем правду
        if (_state.IpsetMode != mode)
        {
            Quiet(() => Select(IpsetSegments, _state.IpsetMode.ToString()));
            _state.Notify("Режим IPSet не изменился — сначала обновите список IP", ToastKind.Warning);
        }

        AppSettings.Save();
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _state.AutoUpdateCheck = AutoUpdateSwitch.IsChecked == true;
        AppSettings.Save();
    }

    private void OnCheckUpdatesOnLaunchToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        AppSettings.Current.CheckUpdatesOnLaunch = CheckUpdatesOnLaunchSwitch.IsChecked == true;
        AppSettings.Save();
    }

    // ---------- Активные фейки ----------

    private void OnDiscordFakeChanged(object sender, SelectionChangedEventArgs e)
        => ApplyFake(DiscordFakeCombo, DiscordFakeFile, "Discord UDP");

    private void OnGameFakeChanged(object sender, SelectionChangedEventArgs e)
        => ApplyFake(GameFakeCombo, GameFakeFile, "Игровой UDP");

    private void ApplyFake(ComboBox combo, string activeFile, string title)
    {
        if (_suppress || combo.SelectedItem is not string chosen) return;

        if (FeatureFlags.ReplaceActiveFake(activeFile, chosen))
        {
            _state.Notify($"{title}: {chosen}. Перезапустите обход, чтобы фейк применился.", ToastKind.Success);
            return;
        }

        _state.Notify($"Не удалось заменить {activeFile}", ToastKind.Error);

        var actual = FeatureFlags.CurrentActiveFake(activeFile);
        Quiet(() => combo.SelectedItem = (combo.ItemsSource as IEnumerable<string>)?
            .FirstOrDefault(i => string.Equals(i, actual, StringComparison.OrdinalIgnoreCase)));
    }

    // ---------- О программе и сброс ----------

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrWhiteSpace(url))
            AppState.OpenExternal(url);
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        const string question =
            "Сбросить настройки приложения к значениям по умолчанию?\n\n" +
            "Стратегия, игровой фильтр и режим IPSet останутся без изменений.";

        var owner = Window.GetWindow(this);
        var answer = owner is null
            ? MessageBox.Show(question, "Zapret Control Center", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, question, "Zapret Control Center", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var defaults = new AppSettings();
        var s = AppSettings.Current;
        s.AutoStartBypass = defaults.AutoStartBypass;
        s.StartWithWindows = defaults.StartWithWindows;
        s.StartMinimized = defaults.StartMinimized;
        s.MinimizeToTray = defaults.MinimizeToTray;
        s.CloseToTray = defaults.CloseToTray;
        s.CheckUpdatesOnLaunch = defaults.CheckUpdatesOnLaunch;
        s.Accent = defaults.Accent;
        s.ReducedMotion = defaults.ReducedMotion;

        Autostart.Set(false);
        AppSettings.Save();
        Fx.ReducedMotion = defaults.ReducedMotion;

        Quiet(LoadValues);
        _state.Notify("Настройки приложения сброшены", ToastKind.Success);
    }

    // ---------- Мелочи ----------

    /// <summary>Выполняет действие с подавлением обработчиков Checked/Unchecked/SelectionChanged.</summary>
    private void Quiet(Action action)
    {
        var previous = _suppress;
        _suppress = true;
        try { action(); }
        finally { _suppress = previous; }
    }

    private static void Select(Panel host, string? value)
    {
        foreach (var child in host.Children)
        {
            if (child is RadioButton rb)
                rb.IsChecked = string.Equals(rb.Tag as string, value, StringComparison.Ordinal);
        }
    }
}

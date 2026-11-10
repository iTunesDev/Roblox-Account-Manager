using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace RobloxAccountManager;

/// <summary>Shell: navigation, persistence, timers, tray and hotkeys. Feature logic lives in the other MainWindow.*.cs files.</summary>
public partial class MainWindow : FluentWindow
{
    private readonly SecureStore _store = new();
    private readonly RobloxApi _api = new();
    private readonly RobloxLauncher _launcher;
    private readonly RegionServerFinder _regionFinder;
    private readonly DiscordNotifier _discord = new();
    private readonly MultiInstance _multi = new();
    private readonly ObservableCollection<Account> _accounts = new();
    private readonly ObservableCollection<Account> _running = new();
    private readonly DispatcherTimer _fastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _trimTimer = new();
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly string[] _startArgs;
    private ICollectionView _view = null!;
    private StoreData _data = new();
    private bool _loading = true;
    private bool _reallyExit;
    private CancellationTokenSource? _launchCts;
    private HotkeyManager? _hotkeys;
    private System.Windows.Forms.NotifyIcon? _tray;

    public MainWindow(string[] args)
    {
        _startArgs = args;
        InitializeComponent();
        _launcher = new RobloxLauncher(_api);
        _regionFinder = new RegionServerFinder(_api, new GeoLocator());

        _view = CollectionViewSource.GetDefaultView(_accounts);
        _view.Filter = FilterAccount;
        AccountList.ItemsSource = _view;
        MonitorGrid.ItemsSource = _running;
        _accounts.CollectionChanged += (_, _) => UpdateAccountsSummary();

        _fastTimer.Tick += (_, _) => OnFastTick();
        _trimTimer.Tick += (_, _) => TrimLowResource();
        _healthTimer.Tick += async (_, _) => await ValidateAsync(_accounts.ToList(), quiet: true);

        SourceInitialized += (_, _) => SetupHotkeys();
        Loaded += OnLoaded;
        Closing += OnClosing;
        LoadData();
    }

    // ---------------------------------------------------------------- startup

    private void LoadData()
    {
        try
        {
            _data = _store.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read the saved accounts file. It may belong to another Windows user.\n\n{ex.Message}",
                "Roblox Account Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            _reallyExit = true;
            Application.Current.Shutdown();
            return;
        }

        foreach (var a in _data.Accounts) _accounts.Add(a);
        var s = _data.Settings;
        PlaceBox.Text = s.LastPlace;
        JobBox.Text = s.LastJobId;
        DelayBox.Value = s.LaunchDelaySeconds;
        MultiBox.IsChecked = s.MultiInstance;
        MaxClientsBox.Value = s.MaxClients;
        CoresBox.Value = s.CoresPerClient;
        TrimBox.Value = s.TrimEveryMinutes;
        MinimizeBox.IsChecked = s.MinimizeLowResource;
        RejoinDelayBox.Value = s.RejoinDelaySeconds;
        RejoinMaxBox.Value = s.MaxRejoinAttempts;
        RejoinDisconnectBox.IsChecked = s.RejoinOnDisconnect;
        WebhookBox.Password = s.DiscordWebhookUrl;
        NotifyDisconnectBox.IsChecked = s.NotifyOnDisconnect;
        NotifyRejoinBox.IsChecked = s.NotifyOnRejoin;
        TrayBox.IsChecked = s.CloseToTray;
        HotkeysBox.IsChecked = s.HotkeysEnabled;
        StartupBox.IsChecked = s.StartWithWindows;
        TitlesBox.IsChecked = s.WindowTitles;
        LightBox.IsChecked = s.LightTheme;
        AutoUpdateBox.IsChecked = s.CheckForUpdates;
        VersionText.Text = $"Version {Updater.CurrentVersion}";
        if (s.LightTheme) ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);

        RefreshGroups();
        RefreshGames();
        RefreshPresets();
        UpdateAccountsSummary();
        UpdateSelectionPanels();
        _loading = false;

        ApplyMultiInstance();
        ApplyTrimTimer();
        SetupTray();
        _fastTimer.Start();
        _healthTimer.Start();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        OnFastTick();
        if (!string.IsNullOrWhiteSpace(PlaceBox.Text)) _ = LookupGameAsync(PlaceBox.Text, quiet: true);
        HandleArgs(_startArgs);
        Updater.CleanUpOldVersion();
        if (_startArgs.Contains("--updated")) SetStatus($"Updated to version {Updater.CurrentVersion}.");
        if (_data.Settings.CheckForUpdates) _ = CheckForUpdateAsync(manual: false);
        await ValidateAsync(_accounts.ToList(), quiet: true);
    }

    /// <summary>Started by Windows at sign-in: stay in the tray but still run the startup preset.</summary>
    public void StartHidden()
    {
        // Create the window handle (needed for hotkeys) without showing anything.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        if (_data.Settings.StartupPresetId is Guid id) RunPresetById(id);
    }

    /// <summary>Command line from this launch or forwarded from a second copy (e.g. a preset shortcut).</summary>
    public void HandleArgs(string[] args)
    {
        if (args.Contains("--exit-for-update")) { ExitApp(); return; }
        if (args.Contains("--show")) ShowFromTray();
        if (SystemIntegration.PresetFromArgs(args) is Guid id) RunPresetById(id);
    }

    // ---------------------------------------------------------------- navigation

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || PageAccounts == null) return;
        var page = rb.Tag as string;
        PageAccounts.Visibility = page == "Accounts" ? Visibility.Visible : Visibility.Collapsed;
        PageLaunch.Visibility = page == "Launch" ? Visibility.Visible : Visibility.Collapsed;
        PageServers.Visibility = page == "Servers" ? Visibility.Visible : Visibility.Collapsed;
        PageGames.Visibility = page == "Games" ? Visibility.Visible : Visibility.Collapsed;
        PagePresets.Visibility = page == "Presets" ? Visibility.Visible : Visibility.Collapsed;
        PageMonitor.Visibility = page == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
        PageVault.Visibility = page == "Vault" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Vault") RefreshVault();
        PageSettings.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Servers") ServersGame.Text = GameName.Text.Length > 0 ? GameName.Text : (PlaceBox.Text.Length > 0 ? PlaceBox.Text : "No game picked. Set one on the Launch page.");
        if (page == "Launch") UpdateSelectionPanels();
    }

    private void GoTo(RadioButton nav) => nav.IsChecked = true;

    // ---------------------------------------------------------------- timers

    /// <summary>Roblox windows that weren't opened by this app (or that we couldn't match to an account), by process id.</summary>
    private readonly Dictionary<int, Account> _unlinked = new();

    /// <summary>Every few seconds: refresh which clients are alive, live stats, auto-rejoin.</summary>
    private void OnFastTick()
    {
        var alive = RobloxLauncher.RunningClientsByStart(); // oldest first

        // 1. Accounts whose window closed.
        foreach (var a in _accounts)
            if (a.RunningPid is int pid && !alive.Contains(pid)) OnClientExited(a);

        // 2. Windows that started late (first launch after a Roblox update can take a while): give each
        //    unclaimed window to the account that launched longest ago and is still waiting for one.
        var claimed = _accounts.Where(a => a.RunningPid != null).Select(a => a.RunningPid!.Value).ToHashSet();
        var waiting = new Queue<Account>(_accounts
            .Where(a => a.RunningPid == null && !a.UserClosed && a.LaunchedAt is DateTime t && DateTime.Now - t < TimeSpan.FromMinutes(5))
            .OrderBy(a => a.LaunchedAt));
        foreach (var pid in alive.Where(p => !claimed.Contains(p)))
        {
            if (waiting.Count == 0) break;
            var a = waiting.Dequeue();
            a.RunningPid = pid;
            a.StartedAt ??= DateTime.Now;
            claimed.Add(pid);
            if (_unlinked.Remove(pid, out var placeholder)) _running.Remove(placeholder);
            if (a.LowResource) _ = ClientOptimizer.ApplyAsync(pid, _data.Settings.CoresPerClient, _data.Settings.MinimizeLowResource);
        }

        // 3. Anything still unclaimed is shown as an unlinked window so it can still be monitored, tiled and closed.
        foreach (var pid in alive.Where(p => !claimed.Contains(p) && !_unlinked.ContainsKey(p)))
            _unlinked[pid] = new Account { Username = "Roblox window", Alias = "Not opened from this app", RunningPid = pid, StartedAt = RobloxLauncher.StartTime(pid) };
        foreach (var pid in _unlinked.Keys.Where(p => !alive.Contains(p) || claimed.Contains(p)).ToList())
        {
            _running.Remove(_unlinked[pid]);
            _unlinked.Remove(pid);
        }

        // 4. Live stats.
        double totalCpu = 0, totalRam = 0;
        foreach (var a in _accounts.Concat(_unlinked.Values))
        {
            if (a.RunningPid is not int pid)
            {
                _running.Remove(a);
                continue;
            }
            var lastCpu = a.LastCpuTime;
            var lastSample = a.LastCpuSample;
            if (ProcessStats.TrySample(pid, ref lastCpu, ref lastSample, out var cpu, out var ram))
            {
                a.LastCpuTime = lastCpu;
                a.LastCpuSample = lastSample;
                a.Cpu = cpu;
                a.MemoryMb = ram;
                totalCpu += cpu;
                totalRam += ram;
            }
            a.RaiseUptime();
            if (_data.Settings.WindowTitles && !_unlinked.ContainsKey(pid)) WindowTools.SetTitle(pid, $"Roblox · {a.Title}");
            if (!_running.Contains(a)) _running.Add(a);
        }

        _ = AutoRejoinTickAsync();

        var n = alive.Count;
        RunningText.Text = $"{n} running";
        UsageText.Text = $"CPU {totalCpu:0}% · RAM {totalRam:N0} MB";
        RunningDot.Opacity = n > 0 ? 1 : 0.3;
        StatRunning.Text = n.ToString();
        StatCpu.Text = $"{totalCpu:0}%";
        StatRam.Text = $"{totalRam:N0} MB";
        MonitorEmpty.Visibility = _running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_tray != null) _tray.Text = $"Roblox Account Manager · {n} running";
        UpdateAccountsSummary();
    }

    private void ApplyTrimTimer()
    {
        _trimTimer.Stop();
        var minutes = _data.Settings.TrimEveryMinutes;
        if (minutes <= 0) return;
        _trimTimer.Interval = TimeSpan.FromMinutes(minutes);
        _trimTimer.Start();
    }

    // ---------------------------------------------------------------- tray and hotkeys

    private void SetupTray()
    {
        try
        {
            var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = iconStream != null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application,
                Text = "Roblox Account Manager",
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => ShowFromTray());
            menu.Items.Add("Tile Roblox windows", null, (_, _) => Dispatcher.Invoke(() => OnTile(this, new RoutedEventArgs())));
            menu.Items.Add("Hide / show Roblox windows", null, (_, _) => Dispatcher.Invoke(() => OnToggleHide(this, new RoutedEventArgs())));
            menu.Items.Add("-");
            menu.Items.Add("Close all Roblox", null, (_, _) => Dispatcher.Invoke(() => OnCloseAll(this, new RoutedEventArgs())));
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
            _tray.ContextMenuStrip = menu;
        }
        catch
        {
            _tray = null;
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    private void SetupHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        if (!_data.Settings.HotkeysEnabled) return;
        _hotkeys = new HotkeyManager(this);
        const uint mods = HotkeyManager.Ctrl | HotkeyManager.Alt;
        var ok = _hotkeys.Register(mods, Key.H, () => OnToggleHide(this, new RoutedEventArgs()))
                 & _hotkeys.Register(mods, Key.T, () => OnTile(this, new RoutedEventArgs()))
                 & _hotkeys.Register(mods, Key.M, ShowFromTray);
        if (!ok) SetStatus("Some hotkeys are already used by another app.");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_reallyExit && _data.Settings.CloseToTray && _tray != null)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2000, "Still running", "Roblox Account Manager is in the tray. Right-click the icon to exit.", System.Windows.Forms.ToolTipIcon.None);
            return;
        }
        SaveSettings();
        WindowTools.ShowAll();
        _multi.Dispose();
        _hotkeys?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Application.Current.Shutdown();
    }

    // ---------------------------------------------------------------- settings

    private void OnSettingNumber(object sender, RoutedEventArgs e) { if (!_loading) { SaveSettings(); ApplyTrimTimer(); } }
    private void OnSettingText(object sender, RoutedEventArgs e) { if (!_loading) SaveSettings(); }

    private void OnSettingToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var hotkeysBefore = _data.Settings.HotkeysEnabled;
        SaveSettings();
        if (hotkeysBefore != _data.Settings.HotkeysEnabled) SetupHotkeys();
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplicationThemeManager.Apply(LightBox.IsChecked == true ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.Mica, true);
        SaveSettings();
    }

    private void OnStartupToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            SystemIntegration.SetStartWithWindows(StartupBox.IsChecked == true);
            SaveSettings();
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't change startup setting: {ex.Message}");
        }
    }

    private void OnStartupPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var chosen = StartupPresetBox.SelectedItem as Preset;
        _data.Settings.StartupPresetId = chosen != null && _data.Presets.Contains(chosen) ? chosen.Id : null;
        Save();
    }

    private async void OnTestWebhook(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (!DiscordNotifier.IsValidUrl(_data.Settings.DiscordWebhookUrl)) { SetStatus("That isn't a Discord webhook URL."); return; }
        await _discord.SendAsync(_data.Settings.DiscordWebhookUrl, "Test alert", "Roblox Account Manager is connected to this channel.", DiscordNotifier.Blue);
        SetStatus("Test alert sent. Check your Discord channel.");
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(_store.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", _store.Folder) { UseShellExecute = true });
    }

    private void OnMultiChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyMultiInstance();
        SaveSettings();
    }

    private void ApplyMultiInstance()
    {
        if (MultiBox.IsChecked == true)
        {
            if (!_multi.Enable()) SetStatus("Roblox is already open. Close it so multiple windows can be turned on.");
        }
        else
        {
            _multi.Disable();
        }
    }

    private static int Num(NumberBox box, int fallback) => box.Value is double v ? (int)Math.Round(v) : fallback;

    private void SaveSettings()
    {
        if (_loading) return;
        var s = _data.Settings;
        s.LastPlace = PlaceBox.Text.Trim();
        s.LastJobId = JobBox.Text.Trim();
        s.LaunchDelaySeconds = Math.Max(0, Num(DelayBox, s.LaunchDelaySeconds));
        s.MaxClients = Math.Max(1, Num(MaxClientsBox, s.MaxClients));
        s.CoresPerClient = Math.Max(1, Num(CoresBox, s.CoresPerClient));
        s.TrimEveryMinutes = Math.Max(0, Num(TrimBox, s.TrimEveryMinutes));
        s.MinimizeLowResource = MinimizeBox.IsChecked == true;
        s.MultiInstance = MultiBox.IsChecked == true;
        s.RejoinDelaySeconds = Math.Max(5, Num(RejoinDelayBox, s.RejoinDelaySeconds));
        s.MaxRejoinAttempts = Math.Max(1, Num(RejoinMaxBox, s.MaxRejoinAttempts));
        s.RejoinOnDisconnect = RejoinDisconnectBox.IsChecked == true;
        s.DiscordWebhookUrl = WebhookBox.Password.Trim();
        s.NotifyOnDisconnect = NotifyDisconnectBox.IsChecked == true;
        s.NotifyOnRejoin = NotifyRejoinBox.IsChecked == true;
        s.CloseToTray = TrayBox.IsChecked == true;
        s.HotkeysEnabled = HotkeysBox.IsChecked == true;
        s.StartWithWindows = StartupBox.IsChecked == true;
        s.WindowTitles = TitlesBox.IsChecked == true;
        s.LightTheme = LightBox.IsChecked == true;
        s.CheckForUpdates = AutoUpdateBox.IsChecked == true;
        Save();
    }

    private void Save()
    {
        _data.Accounts = _accounts.ToList();
        try { _store.Save(_data); }
        catch (Exception ex) { SetStatus($"Couldn't save: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- status

    private void SetStatus(string text) => StatusText.Text = text;

    private void SetBusy(bool busy)
    {
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.IsEnabled = JoinButton.IsEnabled = !busy;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _launchCts?.Cancel();

    private void Notify(string title, string message, int color)
    {
        var url = _data.Settings.DiscordWebhookUrl;
        if (!string.IsNullOrWhiteSpace(url)) _ = _discord.SendAsync(url, title, message, color);
    }
}

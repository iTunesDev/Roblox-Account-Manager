using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;

namespace RobloxAccountManager;

/// <summary>Game lookup, favorites and recents, server browser and presets.</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<ServerRow> _servers = new();
    private PlaceInfo? _currentGame;

    // ---------------------------------------------------------------- game lookup

    private void OnLookupGame(object sender, RoutedEventArgs e) => _ = LookupGameAsync(PlaceBox.Text, quiet: false);

    private void OnPlaceLostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        _ = LookupGameAsync(PlaceBox.Text, quiet: true);
    }

    private async Task LookupGameAsync(string input, bool quiet)
    {
        if (!GameTarget.TryParse(input, null, out var target, out var error))
        {
            GamePreview.Visibility = Visibility.Collapsed;
            _currentGame = null;
            if (!quiet) SetStatus(error);
            return;
        }
        if (_currentGame?.PlaceId == target!.PlaceId) return;
        var info = await _api.GetPlaceInfoAsync(target.PlaceId);
        if (info == null) { if (!quiet) SetStatus("Couldn't find that game."); return; }

        _currentGame = info;
        GameName.Text = info.Name;
        GameMeta.Text = $"Place {info.PlaceId} · {info.Playing:N0} playing" +
                        (target.PrivateServerLinkCode != null ? " · private server link" : "");
        GameIcon.Source = info.IconUrl != null ? new BitmapImage(new Uri(info.IconUrl)) : null;
        GamePreview.Visibility = Visibility.Visible;
        UpdateFavButton();
        ServersGame.Text = info.Name;
    }

    private SavedGame GetOrAddGame(long placeId)
    {
        var g = _data.Games.FirstOrDefault(x => x.PlaceId == placeId);
        if (g != null) return g;
        g = new SavedGame { PlaceId = placeId, Name = _currentGame?.PlaceId == placeId ? _currentGame.Name : $"Place {placeId}",
                            IconUrl = _currentGame?.PlaceId == placeId ? _currentGame.IconUrl : null };
        _data.Games.Add(g);
        return g;
    }

    private void RememberGame(long placeId)
    {
        var g = GetOrAddGame(placeId);
        g.LastPlayed = DateTime.Now;
        if (g.IconUrl == null) _ = FillGameInfoAsync(g);
        // Keep favourites plus the 12 most recent.
        var keep = _data.Games.Where(x => x.Favorite).Concat(_data.Games.OrderByDescending(x => x.LastPlayed).Take(12)).Distinct().ToList();
        _data.Games = keep;
        RefreshGames();
    }

    private async Task FillGameInfoAsync(SavedGame g)
    {
        var info = await _api.GetPlaceInfoAsync(g.PlaceId);
        if (info == null) return;
        g.Name = info.Name;
        g.IconUrl = info.IconUrl;
        RefreshGames();
        Save();
    }

    private void RefreshGames()
    {
        var favs = _data.Games.Where(g => g.Favorite).OrderBy(g => g.Name).ToList();
        FavoriteGames.ItemsSource = favs;
        NoFavorites.Visibility = favs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentGames.ItemsSource = _data.Games.Where(g => g.LastPlayed != default).OrderByDescending(g => g.LastPlayed).Take(12).ToList();
    }

    private void UpdateFavButton()
    {
        var fav = _currentGame != null && _data.Games.Any(g => g.PlaceId == _currentGame.PlaceId && g.Favorite);
        FavButton.Appearance = fav ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        FavButton.ToolTip = fav ? "Remove from favorites" : "Add to favorites";
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (_currentGame == null) return;
        var g = GetOrAddGame(_currentGame.PlaceId);
        g.Favorite = !g.Favorite;
        g.Name = _currentGame.Name;
        g.IconUrl = _currentGame.IconUrl;
        Save();
        RefreshGames();
        UpdateFavButton();
        SetStatus(g.Favorite ? $"Added {g.Name} to favorites." : $"Removed {g.Name} from favorites.");
    }

    private void OnUseGame(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SavedGame g) return;
        PlaceBox.Text = g.PlaceId.ToString();
        JobBox.Text = "";
        SaveSettings();
        _ = LookupGameAsync(PlaceBox.Text, quiet: true);
        GoTo(NavLaunch);
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        _data.Games = _data.Games.Where(g => g.Favorite).ToList();
        foreach (var g in _data.Games) g.LastPlayed = default;
        Save();
        RefreshGames();
    }

    // ---------------------------------------------------------------- server browser

    private async void OnLoadServers(object sender, RoutedEventArgs e)
    {
        if (!TryReadTarget(required: true, out var target)) return;
        ServerGrid.ItemsSource = _servers;
        _servers.Clear();
        SetStatus("Loading servers...");
        try
        {
            string? cursor = null;
            do
            {
                var (page, next) = await _api.GetPublicServersAsync(target!.PlaceId, cursor);
                foreach (var s in page)
                    _servers.Add(new ServerRow { JobId = s.JobId, Playing = s.Playing, MaxPlayers = s.MaxPlayers, Ping = s.Ping });
                cursor = next;
            } while (cursor != null && _servers.Count < 200);
            SetStatus($"{_servers.Count} servers with free slots. Press \"Find regions\" to see where they're hosted.");
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't load servers: {ex.Message}");
        }
    }

    private async void OnFindRegions(object sender, RoutedEventArgs e)
    {
        if (_servers.Count == 0) { SetStatus("Load servers first."); return; }
        if (!TryReadTarget(required: true, out var target)) return;
        var scanner = _accounts.FirstOrDefault(a => a.Status == AccountStatus.Valid);
        if (scanner == null) { SetStatus("Add a signed-in account first. It's needed to look up server locations."); return; }

        await RunBusyAsync(async ct =>
        {
            var rows = _servers.Where(s => s.Region == "–").Take(60).ToList();
            var regions = await _regionFinder.LocateServersAsync(scanner.Cookie, target!.PlaceId, rows.Select(r => r.JobId), new Progress<string>(SetStatus), ct);
            foreach (var r in rows) r.Region = regions.TryGetValue(r.JobId, out var reg) ? reg : "Unknown";
            SetStatus($"Found regions for {regions.Count} of {rows.Count} servers.");
        });
    }

    private async void OnSendToServer(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is not ServerRow row) { SetStatus("Pick a server in the list first."); return; }
        if (!TryReadTarget(required: true, out var target)) return;
        var accounts = Selected().Where(a => !a.IsRunning).ToList();
        if (accounts.Count == 0) { SetStatus("Select accounts on the Accounts page first."); return; }
        if (accounts.Count > row.FreeSlots) SetStatus($"Only {row.FreeSlots} slot(s) free, so some accounts may not get in.");
        if (!EnsureMultiInstance(accounts.Count)) return;

        var t = new GameTarget(target!.PlaceId, row.JobId);
        await RunBusyAsync(async ct =>
        {
            var n = await LaunchSequenceAsync(accounts.Select(a => (a, t)).ToList(), ct);
            SetStatus($"Sent {n} account(s) to that server.");
        });
    }

    // ---------------------------------------------------------------- presets

    private record PresetView(Preset Preset, string Description);

    private void RefreshPresets()
    {
        PresetList.ItemsSource = _data.Presets.Select(p =>
        {
            var names = p.AccountIds.Select(id => _accounts.FirstOrDefault(a => a.Id == id)?.Title).Where(n => n != null).ToList();
            var game = GameTarget.TryParse(p.Place, null, out var t, out _) ? _data.Games.FirstOrDefault(g => g.PlaceId == t!.PlaceId)?.Name ?? p.Place : p.Place;
            return new PresetView(p, $"{game} · {p.Summary(names.Count)} · {string.Join(", ", names.Take(5))}{(names.Count > 5 ? "..." : "")}");
        }).ToList();
        NoPresets.Visibility = _data.Presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var startup = _data.Settings.StartupPresetId;
        var wasLoading = _loading;
        _loading = true;
        StartupPresetBox.ItemsSource = new[] { new Preset { Name = "(none)" } }.Concat(_data.Presets).ToList();
        StartupPresetBox.SelectedItem = _data.Presets.FirstOrDefault(p => p.Id == startup) ?? ((List<Preset>)StartupPresetBox.ItemsSource)[0];
        _loading = wasLoading;
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) { SetStatus("Select the accounts for this preset first."); return; }
        if (!TryReadTarget(required: true, out _)) return;

        var name = Microsoft.VisualBasic.Interaction.InputBox("Name this preset:", "Save preset", _currentGame?.Name ?? "My preset");
        if (string.IsNullOrWhiteSpace(name)) return;

        var together = System.Windows.MessageBox.Show(this,
            "Should these accounts join the same server?\n\nYes = same server\nNo = each joins separately",
            "Save preset", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

        _data.Presets.Add(new Preset
        {
            Name = name.Trim(),
            AccountIds = sel.Select(a => a.Id).ToList(),
            Place = PlaceBox.Text.Trim(),
            JobId = JobBox.Text.Trim(),
            JoinTogether = together,
        });
        Save();
        RefreshPresets();
        SetStatus($"Saved preset \"{name.Trim()}\".");
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Preset p) return;
        _data.Presets.Remove(p);
        if (_data.Settings.StartupPresetId == p.Id) _data.Settings.StartupPresetId = null;
        Save();
        RefreshPresets();
    }

    private void OnPresetShortcut(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Preset p) return;
        try
        {
            SystemIntegration.CreatePresetShortcut(p.Name, p.Id);
            SetStatus($"Put a \"{p.Name}\" shortcut on your desktop.");
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't create the shortcut: {ex.Message}");
        }
    }

    private void OnRunPreset(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Preset p) RunPreset(p);
    }

    private void RunPresetById(Guid id)
    {
        var p = _data.Presets.FirstOrDefault(x => x.Id == id);
        if (p == null) { SetStatus("That preset no longer exists."); return; }
        RunPreset(p);
    }

    private async void RunPreset(Preset p)
    {
        var accounts = p.AccountIds.Select(id => _accounts.FirstOrDefault(a => a.Id == id)).OfType<Account>().Where(a => !a.IsRunning).ToList();
        if (accounts.Count == 0) { SetStatus($"Everyone in \"{p.Name}\" is already running (or was removed)."); return; }
        if (!GameTarget.TryParse(p.Place, p.JobId, out var target, out var error)) { SetStatus(error); return; }
        if (!EnsureMultiInstance(accounts.Count)) return;

        SetStatus($"Running preset \"{p.Name}\"...");
        await RunBusyAsync(ct => p.JoinTogether
            ? JoinTogetherAsync(accounts, target, ct)
            : LaunchSeparatelyAsync(accounts, target!, ct));
    }
}

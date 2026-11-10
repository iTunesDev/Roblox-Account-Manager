using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;

namespace RobloxAccountManager;

/// <summary>Launching: separate launches, session joiner, region picking, friend follow, server hop and auto-rejoin.</summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- shared helpers

    private int LaunchDelay => Math.Max(0, Num(DelayBox, _data.Settings.LaunchDelaySeconds));

    /// <summary>Checks multi-window mode is usable for what's about to start.</summary>
    private bool EnsureMultiInstance(int newClients)
    {
        if (newClients <= 1 && RobloxLauncher.CountRunningClients() == 0) return true;
        if (MultiBox.IsChecked != true)
        {
            SetStatus("Turn on \"Multiple windows\" (bottom left) to run more than one account.");
            return false;
        }
        ApplyMultiInstance();
        if (_multi.IsEnabled) return true;
        SetStatus("Multiple windows couldn't turn on because Roblox was opened before this app. Close all Roblox windows and try again.");
        return false;
    }

    private bool TryReadTarget(bool required, out GameTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(PlaceBox.Text) && !required) return true;
        if (GameTarget.TryParse(PlaceBox.Text, JobBox.Text, out target, out var error)) return true;
        SetStatus(error);
        return false;
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        if (_launchCts != null) { SetStatus("Already launching. Press Stop first."); return; }
        _launchCts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await work(_launchCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
        }
        catch (Exception ex)
        {
            SetStatus($"Something went wrong: {ex.Message}");
        }
        finally
        {
            _launchCts.Dispose();
            _launchCts = null;
            SetBusy(false);
            Save();
        }
    }

    private bool HasRoomForAnother()
    {
        var max = Math.Max(1, _data.Settings.MaxClients);
        if (RobloxLauncher.CountRunningClients() < max) return true;
        SetStatus($"Reached the limit of {max} Roblox windows. Raise it in Settings to open more.");
        return false;
    }

    /// <summary>Launches one account and applies its window settings. Returns false on failure.</summary>
    private async Task<bool> LaunchOneAsync(Account a, GameTarget target)
    {
        try
        {
            a.UserClosed = false;
            a.LastTarget = target;
            a.NotInGameStrikes = 0;
            a.LaunchedAt = DateTime.Now;
            var pid = await _launcher.LaunchAsync(a.Cookie, target);
            // Another tick may already have matched a late window to this account.
            if (pid != null && _accounts.Any(o => o != a && o.RunningPid == pid)) pid = null;
            if (pid != null)
            {
                if (_unlinked.Remove(pid.Value, out var placeholder)) _running.Remove(placeholder);
                a.RunningPid = pid;
            }
            a.StartedAt = DateTime.Now;
            a.LastCpuSample = default;
            if (pid != null && a.LowResource)
                _ = ClientOptimizer.ApplyAsync(pid.Value, _data.Settings.CoresPerClient, _data.Settings.MinimizeLowResource);
            RememberGame(target.PlaceId);
            return true;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("expired")) a.Status = AccountStatus.Invalid;
            SetStatus($"{a.Username}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Launches accounts one after another, each with its own target.</summary>
    private async Task<int> LaunchSequenceAsync(IList<(Account Account, GameTarget Target)> plan, CancellationToken ct)
    {
        var launched = 0;
        for (var i = 0; i < plan.Count && !ct.IsCancellationRequested; i++)
        {
            if (!HasRoomForAnother()) break;
            var (a, t) = plan[i];
            SetStatus($"Launching {a.Title} ({i + 1}/{plan.Count})...");
            if (await LaunchOneAsync(a, t)) launched++;
            if (i < plan.Count - 1) await Task.Delay(TimeSpan.FromSeconds(LaunchDelay), ct);
        }
        return launched;
    }

    // ---------------------------------------------------------------- launch separately

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        var selected = Selected().Where(a => !a.IsRunning).ToList();
        if (selected.Count == 0) { SetStatus("Select accounts that aren't already running."); return; }
        if (!TryReadTarget(required: true, out var target) || !EnsureMultiInstance(selected.Count)) return;
        SaveSettings();
        await RunBusyAsync(ct => LaunchSeparatelyAsync(selected, target!, ct));
    }

    private async Task LaunchSeparatelyAsync(List<Account> accounts, GameTarget target, CancellationToken ct)
    {
        var assignments = new Dictionary<Account, GameTarget>();
        var regional = accounts.Where(a => a.Region != Regions.Any).ToList();
        if (regional.Count > 0 && target.JobId == null && target.PrivateServerLinkCode == null)
            assignments = await AssignRegionServersAsync(target.PlaceId, regional, ct);

        var plan = accounts.Select(a => (a, assignments.TryGetValue(a, out var t) ? t : target)).ToList();
        var n = await LaunchSequenceAsync(plan, ct);
        SetStatus($"Launched {n} of {accounts.Count} account(s).");
    }

    /// <summary>Finds a public server in each account's region, packing accounts of the same region together.</summary>
    private async Task<Dictionary<Account, GameTarget>> AssignRegionServersAsync(long placeId, List<Account> accounts, CancellationToken ct, int minFreeSlots = 1)
    {
        var result = new Dictionary<Account, GameTarget>();
        var scanner = accounts.FirstOrDefault(a => a.Status != AccountStatus.Invalid) ?? accounts[0];
        var wanted = accounts.Select(a => a.Region).ToHashSet();
        var found = await _regionFinder.FindAsync(scanner.Cookie, placeId, wanted, new Progress<string>(SetStatus), ct: ct);

        foreach (var group in accounts.GroupBy(a => a.Region))
        {
            var queue = new Queue<Account>(group);
            foreach (var s in found.Where(f => f.Region == group.Key && f.FreeSlots >= minFreeSlots))
            {
                var slots = s.FreeSlots;
                while (slots-- > 0 && queue.Count > 0) result[queue.Dequeue()] = new GameTarget(placeId, s.JobId);
                if (queue.Count == 0) break;
            }
            if (queue.Count > 0)
                SetStatus($"No open {group.Key} server found for {string.Join(", ", queue.Select(a => a.Username))}. They'll join any server.");
        }
        return result;
    }

    // ---------------------------------------------------------------- session joiner

    private async void OnJoinTogether(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0) { SetStatus("Select the accounts to join together."); return; }
        if (!TryReadTarget(required: false, out var target) || !EnsureMultiInstance(selected.Count)) return;
        SaveSettings();
        await RunBusyAsync(ct => JoinTogetherAsync(selected, target, ct));
    }

    /// <summary>
    /// Gets every account into one server. If one is already in a game it leads and the rest follow;
    /// otherwise the first account joins and the rest follow it once it has loaded in.
    /// </summary>
    private async Task JoinTogetherAsync(List<Account> accounts, GameTarget? target, CancellationToken ct)
    {
        SetStatus("Looking for an account that's already in a game...");
        Account? leader = null;
        GameTarget? shared = null;
        foreach (var a in accounts)
        {
            try
            {
                var p = await _api.GetOwnPresenceAsync(a.Cookie, a.UserId);
                if (!p.InGame || (target != null && p.PlaceId != target.PlaceId)) continue;
                leader = a;
                shared = new GameTarget(p.PlaceId, p.JobId);
                break;
            }
            catch { /* try the next one */ }
        }

        if (shared == null && target?.PrivateServerLinkCode != null) shared = target;
        if (shared == null && target?.JobId != null) shared = target;

        if (shared == null)
        {
            if (target == null) { SetStatus("Nobody selected is in a game yet. Enter a game on the Launch page."); return; }
            leader = accounts.FirstOrDefault(a => !a.IsRunning) ?? accounts[0];
            var leaderTarget = target;
            if (leader.Region != Regions.Any)
            {
                var map = await AssignRegionServersAsync(target.PlaceId, new List<Account> { leader }, ct, minFreeSlots: accounts.Count);
                if (map.TryGetValue(leader, out var rt)) leaderTarget = rt;
            }
            SetStatus($"Launching {leader.Title} first...");
            if (!await LaunchOneAsync(leader, leaderTarget)) return;

            SetStatus($"Waiting for {leader.Title} to load into a server...");
            shared = await WaitForServerAsync(leader, TimeSpan.FromMinutes(3), ct);
            if (shared == null) { SetStatus($"{leader.Title} didn't reach a server in time, so the others weren't launched."); return; }
            leader.LastTarget = shared;
            await Task.Delay(TimeSpan.FromSeconds(LaunchDelay), ct);
        }

        var rest = accounts.Where(a => a != leader && !a.IsRunning).Select(a => (a, shared)).ToList();
        var n = await LaunchSequenceAsync(rest, ct);
        SetStatus($"Sent {n} account(s) to the same server{(leader != null ? $" as {leader.Title}" : "")}.");
    }

    private async Task<GameTarget?> WaitForServerAsync(Account a, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(3000, ct);
            try
            {
                var p = await _api.GetOwnPresenceAsync(a.Cookie, a.UserId);
                if (p.InGame) return new GameTarget(p.PlaceId, p.JobId);
            }
            catch { }
        }
        return null;
    }

    // ---------------------------------------------------------------- friend follow

    private async void OnFollowFriend(object sender, RoutedEventArgs e)
    {
        var selected = Selected().Where(a => !a.IsRunning).ToList();
        var name = FriendBox.Text.Trim();
        if (name.Length == 0) { SetStatus("Type your friend's username."); return; }
        if (selected.Count == 0) { SetStatus("Select the accounts that should follow."); return; }
        if (!EnsureMultiInstance(selected.Count)) return;

        await RunBusyAsync(async ct =>
        {
            SetStatus($"Finding {name}...");
            var friendId = await _api.GetUserIdByUsernameAsync(name);
            if (friendId == null) { SetStatus($"No Roblox user called {name}."); return; }

            // Any selected account that's allowed to see where the friend is will do.
            GameTarget? where = null;
            foreach (var a in selected)
            {
                var p = await _api.GetPresenceAsync(a.Cookie, friendId.Value);
                if (p.InGame && p.JobId != null) { where = new GameTarget(p.PlaceId, p.JobId); break; }
                if (p.PlaceId != 0 && where == null) where = new GameTarget(p.PlaceId);
            }
            if (where == null) { SetStatus($"{name} isn't in a game, or their privacy settings hide it."); return; }
            if (where.JobId == null) SetStatus($"{name}'s server is hidden, so the accounts will join the same game but maybe not the same server.");

            var n = await LaunchSequenceAsync(selected.Select(a => (a, where)).ToList(), ct);
            SetStatus($"Sent {n} account(s) to {name}.");
        });
    }

    // ---------------------------------------------------------------- server hop

    /// <summary>Moves the selected running accounts (or all running ones) to a different public server of the same game.</summary>
    private async void OnServerHop(object sender, RoutedEventArgs e)
    {
        var accounts = MonitorGrid.SelectedItems.Cast<Account>().Where(a => _accounts.Contains(a)).ToList();
        if (accounts.Count == 0) accounts = Selected().Where(a => a.IsRunning).ToList();
        if (accounts.Count == 0) { SetStatus("Select running accounts to move. Windows not opened from this app can't be moved."); return; }

        await RunBusyAsync(async ct =>
        {
            var lead = accounts[0];
            var p = await _api.GetOwnPresenceAsync(lead.Cookie, lead.UserId);
            var placeId = p.PlaceId != 0 ? p.PlaceId : lead.LastTarget?.PlaceId ?? 0;
            if (placeId == 0) { SetStatus("Couldn't tell which game they're in."); return; }

            SetStatus("Finding another server with room...");
            var (servers, _) = await _api.GetPublicServersAsync(placeId, null);
            var current = accounts.Select(a => a.LastTarget?.JobId).Append(p.JobId).Where(j => j != null).ToHashSet();
            var next = servers.Where(s => !current.Contains(s.JobId) && s.MaxPlayers - s.Playing >= accounts.Count)
                              .OrderByDescending(s => s.Playing).FirstOrDefault();
            if (next == null) { SetStatus("No other server has room for all of them right now."); return; }

            foreach (var a in accounts)
            {
                a.UserClosed = true; // don't let auto-rejoin race us
                if (a.RunningPid is int pid) RobloxLauncher.Close(pid);
                a.RunningPid = null;
            }
            await Task.Delay(2000, ct);
            var target = new GameTarget(placeId, next.JobId);
            var n = await LaunchSequenceAsync(accounts.Select(a => (a, target)).ToList(), ct);
            SetStatus($"Moved {n} account(s) to a new server ({next.Playing}/{next.MaxPlayers} players).");
        });
    }

    // ---------------------------------------------------------------- auto-rejoin

    private void OnClientExited(Account a)
    {
        a.RunningPid = null;
        a.StartedAt = null;
        a.Cpu = 0;
        a.MemoryMb = 0;
        if (a.UserClosed || !a.AutoRejoin || a.LastTarget == null) return;
        a.RejoinAfter = DateTime.Now.AddSeconds(_data.Settings.RejoinDelaySeconds);
        if (_data.Settings.NotifyOnDisconnect) Notify("Account dropped", $"{a.Title}'s Roblox window closed. Rejoining shortly.", DiscordNotifier.Red);
        SetStatus($"{a.Title}'s window closed. Rejoining in {_data.Settings.RejoinDelaySeconds}s...");
    }

    private bool _rejoinBusy;
    private DateTime _lastPresenceSweep = DateTime.MinValue;

    /// <summary>
    /// Runs on the fast timer. Relaunches accounts whose window closed, and (once a minute) checks that
    /// running auto-rejoin accounts are actually still in a game, which catches the disconnect screen.
    /// </summary>
    private async Task AutoRejoinTickAsync()
    {
        if (_rejoinBusy || _launchCts != null) return;
        _rejoinBusy = true;
        try
        {
            if (_data.Settings.RejoinOnDisconnect && DateTime.Now - _lastPresenceSweep > TimeSpan.FromSeconds(60))
            {
                _lastPresenceSweep = DateTime.Now;
                foreach (var a in _accounts.Where(a => a.AutoRejoin && a.IsRunning && !a.UserClosed && a.StartedAt < DateTime.Now.AddMinutes(-2)).ToList())
                {
                    try
                    {
                        var p = await _api.GetOwnPresenceAsync(a.Cookie, a.UserId);
                        a.NotInGameStrikes = p.InGame ? 0 : a.NotInGameStrikes + 1;
                        if (p.InGame) a.RejoinAttempts = 0;
                        if (a.NotInGameStrikes >= 2 && a.RunningPid is int pid)
                        {
                            if (_data.Settings.NotifyOnDisconnect) Notify("Account disconnected", $"{a.Title} dropped out of the game. Rejoining.", DiscordNotifier.Red);
                            RobloxLauncher.Close(pid);
                            OnClientExited(a);
                        }
                    }
                    catch { }
                }
            }

            foreach (var a in _accounts.Where(a => a.RejoinAfter is DateTime t && t <= DateTime.Now).ToList())
            {
                a.RejoinAfter = null;
                if (a.IsRunning || a.UserClosed || !a.AutoRejoin || a.LastTarget is not GameTarget last) continue;
                if (a.RejoinAttempts >= _data.Settings.MaxRejoinAttempts)
                {
                    SetStatus($"Gave up rejoining {a.Title} after {a.RejoinAttempts} tries.");
                    Notify("Rejoin failed", $"Gave up rejoining {a.Title} after {a.RejoinAttempts} tries.", DiscordNotifier.Red);
                    continue;
                }
                if (!HasRoomForAnother()) continue;

                a.RejoinAttempts++;
                // First try the same server, after that any server of the same game (the old one may have shut down).
                var target = a.RejoinAttempts == 1 ? last : new GameTarget(last.PlaceId, null, last.PrivateServerLinkCode);
                SetStatus($"Rejoining {a.Title} (try {a.RejoinAttempts})...");
                if (await LaunchOneAsync(a, target))
                {
                    a.LastTarget = last;
                    if (_data.Settings.NotifyOnRejoin) Notify("Rejoined", $"{a.Title} was relaunched (try {a.RejoinAttempts}).", DiscordNotifier.Green);
                }
                else
                {
                    a.RejoinAfter = DateTime.Now.AddSeconds(_data.Settings.RejoinDelaySeconds * 2);
                }
            }
        }
        finally
        {
            _rejoinBusy = false;
        }
    }

    // ---------------------------------------------------------------- per-row and window actions

    private async void OnQuickLaunch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account a) return;
        if (!TryReadTarget(required: true, out var target)) { GoTo(NavLaunch); return; }
        if (!EnsureMultiInstance(1) || !HasRoomForAnother()) return;
        await RunBusyAsync(ct => LaunchSeparatelyAsync(new List<Account> { a }, target!, ct));
    }

    private void OnQuickClose(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Account a) CloseAccounts(new[] { a });
    }

    private void CloseAccounts(IEnumerable<Account> accounts)
    {
        var n = 0;
        foreach (var a in accounts)
        {
            a.UserClosed = true;
            a.RejoinAfter = null;
            if (a.RunningPid is int pid && RobloxLauncher.Close(pid)) n++;
            a.RunningPid = null;
        }
        SetStatus($"Closed {n} Roblox window(s).");
    }

    private void OnCloseSelected(object sender, RoutedEventArgs e)
    {
        var list = MonitorGrid.SelectedItems.Cast<Account>().ToList();
        if (list.Count == 0) list = Selected();
        if (list.Count == 0) { SetStatus("Select a row in the list first."); return; }
        CloseAccounts(list);
    }

    private void OnCloseAll(object sender, RoutedEventArgs e)
    {
        foreach (var a in _accounts) { a.UserClosed = true; a.RejoinAfter = null; a.RunningPid = null; }
        var n = RobloxLauncher.CloseAllClients();
        foreach (var u in _unlinked.Values) _running.Remove(u);
        _unlinked.Clear();
        SetStatus($"Closed {n} Roblox window(s).");
    }

    private void OnTile(object sender, RoutedEventArgs e)
    {
        var n = WindowTools.Tile();
        HideButton.Content = "Hide all windows";
        SetStatus(n == 0 ? "No Roblox windows to arrange." : $"Arranged {n} window(s).");
    }

    private void OnToggleHide(object sender, RoutedEventArgs e)
    {
        if (WindowTools.AreHidden)
        {
            WindowTools.ShowAll();
            HideButton.Content = "Hide all windows";
            SetStatus("Roblox windows shown.");
        }
        else
        {
            WindowTools.HideAll();
            HideButton.Content = "Show all windows";
            SetStatus("Roblox windows hidden. They keep running. Press Ctrl+Alt+H to bring them back.");
        }
    }

    private void TrimLowResource()
    {
        foreach (var a in _accounts)
            if (a.LowResource && a.RunningPid is int pid) ClientOptimizer.TrimMemory(pid);
    }

    /// <summary>Optimizes the rows selected in Monitor, or every low-resource account if none are selected.</summary>
    private void OnOptimizeNow(object sender, RoutedEventArgs e)
    {
        var picked = MonitorGrid.SelectedItems.Cast<Account>().ToList();
        var targets = picked.Count > 0 ? picked : _accounts.Where(a => a.LowResource).ToList();
        var n = 0;
        foreach (var a in targets)
        {
            if (a.RunningPid is not int pid) continue;
            _ = ClientOptimizer.ApplyAsync(pid, _data.Settings.CoresPerClient, _data.Settings.MinimizeLowResource);
            ClientOptimizer.TrimMemory(pid);
            n++;
        }
        SetStatus(n == 0 ? "Nothing to optimize. Select rows in the list, or turn on low-resource mode for an account." : $"Optimized {n} running window(s).");
    }
}

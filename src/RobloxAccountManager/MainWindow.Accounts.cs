using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;
using RobloxAccountManager.Views;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace RobloxAccountManager;

/// <summary>Accounts page: adding, importing, editing, filtering and checking logins.</summary>
public partial class MainWindow
{
    private const string AllGroups = "All groups";

    private List<Account> Selected() => AccountList.SelectedItems.Cast<Account>().ToList();

    private bool FilterAccount(object o)
    {
        if (o is not Account a) return false;
        var q = SearchBox?.Text?.Trim() ?? "";
        if (q.Length > 0 &&
            !a.Username.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !a.Alias.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !a.Notes.Contains(q, StringComparison.OrdinalIgnoreCase))
            return false;
        var g = GroupFilter?.SelectedItem as string;
        return g == null || g == AllGroups || a.Group == g;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => _view?.Refresh();

    private void RefreshGroups()
    {
        var groups = _accounts.Select(a => a.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().OrderBy(g => g).ToList();
        var current = GroupFilter.SelectedItem as string;
        GroupFilter.ItemsSource = new[] { AllGroups }.Concat(groups).ToList();
        GroupFilter.SelectedItem = current != null && groups.Contains(current) ? current : AllGroups;
        GroupEdit.ItemsSource = groups;
        BulkGroup.ItemsSource = groups;
        QuickGroup.ItemsSource = groups;
    }

    private void UpdateAccountsSummary()
    {
        var running = _accounts.Count(a => a.IsRunning);
        AccountsSummary.Text = _accounts.Count == 0 ? "No accounts yet" : $"{_accounts.Count} accounts · {running} running";
        EmptyHint.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- adding

    private void OnAddMenu(object sender, RoutedEventArgs e)
    {
        if (AddButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = AddButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void OnAddLogin(object sender, RoutedEventArgs e)
    {
        var w = new LoginWindow { Owner = this };
        if (w.ShowDialog() == true && w.Cookie != null) await AddFromLoginWindowAsync(w);
    }

    /// <summary>Adds the signed-in account and stores whatever login details were typed into the vault.</summary>
    private async Task AddFromLoginWindowAsync(LoginWindow w)
    {
        if (!await AddAccountAsync(w.Cookie!)) return;
        var acc = _accounts.LastOrDefault(a => a.Cookie == w.Cookie);
        if (acc == null || string.IsNullOrEmpty(w.CapturedPassword)) return;
        acc.LoginName = string.IsNullOrWhiteSpace(w.CapturedLogin) ? acc.Username : w.CapturedLogin!;
        acc.Password = w.CapturedPassword!;
        Save();
        SetStatus($"Added {acc.Username} and saved the password to your vault.");
    }

    /// <summary>Opens Roblox's own sign-up page; the user fills it in (and solves the captcha) themselves.</summary>
    private async void OnCreateAccount(object sender, RoutedEventArgs e)
    {
        var w = new LoginWindow(signUp: true) { Owner = this };
        if (w.ShowDialog() == true && w.Cookie != null) await AddFromLoginWindowAsync(w);
    }

    private async void OnAddCookie(object sender, RoutedEventArgs e)
    {
        var w = new CookieWindow { Owner = this };
        if (w.ShowDialog() == true) await AddAccountAsync(w.Cookie);
    }

    /// <summary>
    /// Imports a text file with one account per line. Accepts plain cookies, ".ROBLOSECURITY=..." lines and
    /// "user:pass:cookie" style lines from other managers; only the cookie part is kept.
    /// </summary>
    private async void OnImportCookies(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", Title = "Import cookies" };
        if (dlg.ShowDialog(this) != true) return;

        var cookieRx = new Regex(@"_\|WARNING:-DO-NOT-SHARE-THIS[^\s:;,""]+", RegexOptions.Compiled);
        var cookies = File.ReadAllLines(dlg.FileName)
            .Select(l => cookieRx.Match(l))
            .Where(m => m.Success)
            .Select(m => m.Value)
            .Distinct()
            .ToList();
        if (cookies.Count == 0) { SetStatus("No Roblox cookies found in that file."); return; }

        var added = 0;
        for (var i = 0; i < cookies.Count; i++)
        {
            SetStatus($"Importing {i + 1}/{cookies.Count}...");
            if (await AddAccountAsync(cookies[i], quiet: true)) added++;
            await Task.Delay(300);
        }
        SetStatus($"Imported {added} of {cookies.Count} account(s). {cookies.Count - added} were expired or invalid.");
    }

    private async Task<bool> AddAccountAsync(string cookie, bool quiet = false)
    {
        if (!quiet) SetStatus("Checking account...");
        RobloxUser? user;
        try { user = await _api.GetAuthenticatedUserAsync(cookie); }
        catch (Exception ex) { if (!quiet) SetStatus($"Couldn't reach Roblox: {ex.Message}"); return false; }

        if (user == null) { if (!quiet) SetStatus("That login isn't valid."); return false; }

        var existing = _accounts.FirstOrDefault(a => a.UserId == user.Id);
        var acc = existing ?? new Account();
        acc.UserId = user.Id;
        acc.Username = user.Name;
        acc.DisplayName = user.DisplayName;
        acc.Cookie = cookie;
        acc.Status = AccountStatus.Valid;
        acc.LastValidated = DateTime.Now;
        if (existing == null) _accounts.Add(acc);
        await RefreshProfileAsync(acc);
        Save();
        RefreshGroups();
        if (!quiet) SetStatus(existing == null ? $"Added {user.Name}." : $"Updated login for {user.Name}.");
        return true;
    }

    private async Task RefreshProfileAsync(Account a)
    {
        a.AvatarUrl = await _api.GetHeadshotUrlAsync(a.UserId) ?? a.AvatarUrl;
        a.Robux = await _api.GetRobuxAsync(a.Cookie, a.UserId) ?? a.Robux;
        if (await _api.GetPremiumAsync(a.Cookie, a.UserId) is bool p) a.Premium = p;
    }

    // ---------------------------------------------------------------- checking

    private async void OnValidateAll(object sender, RoutedEventArgs e) => await ValidateAsync(_accounts.ToList());

    /// <summary>Account health: confirms each login still works and refreshes avatar, Robux and Premium.</summary>
    private async Task ValidateAsync(List<Account> accounts, bool quiet = false)
    {
        if (accounts.Count == 0) return;
        if (!quiet) SetStatus("Checking logins...");
        var expired = new List<string>();
        foreach (var a in accounts)
        {
            var wasValid = a.Status != AccountStatus.Invalid;
            a.Status = AccountStatus.Checking;
            try
            {
                var user = await _api.GetAuthenticatedUserAsync(a.Cookie);
                if (user == null)
                {
                    a.Status = AccountStatus.Invalid;
                    if (wasValid) expired.Add(a.Username);
                    continue;
                }
                a.Username = user.Name;
                a.DisplayName = user.DisplayName;
                a.Status = AccountStatus.Valid;
                a.LastValidated = DateTime.Now;
                await RefreshProfileAsync(a);
            }
            catch
            {
                a.Status = AccountStatus.Unknown;
            }
        }
        Save();
        if (expired.Count > 0)
        {
            SetStatus($"{expired.Count} login(s) expired: {string.Join(", ", expired)}. Sign in to them again.");
            Notify("Logins expired", string.Join(", ", expired) + " need to sign in again.", DiscordNotifier.Red);
        }
        else if (!quiet)
        {
            SetStatus("All logins OK.");
        }
    }

    // ---------------------------------------------------------------- editing

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionPanels();

    private void UpdateSelectionPanels()
    {
        var sel = Selected();
        DetailsPanel.DataContext = sel.Count == 1 ? sel[0] : null;
        DetailsPanel.Visibility = sel.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        MultiPanel.Visibility = sel.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        NoSelection.Visibility = sel.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MultiTitle.Text = $"{sel.Count} accounts selected";
        LaunchSelection.Text = sel.Count == 0
            ? "None selected. Pick them on the Accounts page."
            : $"{sel.Count} selected: {string.Join(", ", sel.Take(6).Select(a => a.Title))}{(sel.Count > 6 ? $" and {sel.Count - 6} more" : "")}";
    }

    private void OnDetailsEdited(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Save();
        RefreshGroups();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        var names = string.Join(", ", selected.Select(a => a.Username));
        if (MessageBox.Show(this, $"Remove {names}? The saved login will be deleted from this PC.", "Remove accounts",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var a in selected) _accounts.Remove(a);
        foreach (var p in _data.Presets) p.AccountIds.RemoveAll(id => selected.Any(a => a.Id == id));
        Save();
        RefreshGroups();
        RefreshPresets();
        SetStatus($"Removed {selected.Count} account(s).");
    }

    private void Bulk(Action<Account> change, string message)
    {
        var sel = Selected();
        foreach (var a in sel) change(a);
        Save();
        RefreshGroups();
        SetStatus($"{message} for {sel.Count} account(s).");
    }

    private void OnBulkGroup(object sender, RoutedEventArgs e)
    {
        var g = BulkGroup.Text.Trim();
        Bulk(a => a.Group = g, g.Length == 0 ? "Cleared group" : $"Group set to {g}");
    }

    private void OnBulkRegion(object sender, SelectionChangedEventArgs e)
    {
        if (BulkRegion.SelectedItem is string r) Bulk(a => a.Region = r, $"Region set to {r}");
    }

    private void OnBulkLowResOn(object sender, RoutedEventArgs e) => Bulk(a => a.LowResource = true, "Low-resource mode on");
    private void OnBulkLowResOff(object sender, RoutedEventArgs e) => Bulk(a => a.LowResource = false, "Low-resource mode off");
    private void OnBulkRejoinOn(object sender, RoutedEventArgs e) => Bulk(a => a.AutoRejoin = true, "Auto-rejoin on");
    private void OnBulkRejoinOff(object sender, RoutedEventArgs e) => Bulk(a => a.AutoRejoin = false, "Auto-rejoin off");

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        AccountList.SelectAll();
        UpdateSelectionPanels();
    }

    private void OnQuickGroup(object sender, SelectionChangedEventArgs e)
    {
        if (QuickGroup.SelectedItem is not string g) return;
        GroupFilter.SelectedItem = AllGroups;
        AccountList.SelectedItems.Clear();
        foreach (var a in _accounts.Where(a => a.Group == g)) AccountList.SelectedItems.Add(a);
        UpdateSelectionPanels();
        QuickGroup.SelectedItem = null;
    }
}

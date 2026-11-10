using System.Windows;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;
using RobloxAccountManager.Views;

namespace RobloxAccountManager;

/// <summary>Password vault: saved logins per account, copy with auto-clearing clipboard, and re-login.</summary>
public partial class MainWindow
{
    private DispatcherTimer? _clipboardTimer;

    private void RefreshVault()
    {
        var q = VaultSearch.Text.Trim();
        VaultList.ItemsSource = _accounts
            .Where(a => q.Length == 0 || a.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || a.LoginName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Title)
            .ToList();
    }

    private void OnVaultSearch(object sender, RoutedEventArgs e) => RefreshVault();

    private void OnVaultEdited(object sender, RoutedEventArgs e) { if (!_loading) Save(); }

    private void OnCopyLogin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account a) return;
        var login = string.IsNullOrWhiteSpace(a.LoginName) ? a.Username : a.LoginName;
        Clipboard.SetText(login);
        SetStatus($"Copied login for {a.Title}.");
    }

    private void OnCopyPassword(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account a) return;
        if (!a.HasPassword) { SetStatus($"No password saved for {a.Title}."); return; }
        CopySecret(a.Password);
        SetStatus($"Copied {a.Title}'s password. It clears from the clipboard in 30 seconds.");
    }

    private void OnGeneratePassword(object sender, RoutedEventArgs e)
    {
        CopySecret(CredentialGenerator.NewPassword());
        SetStatus("Copied a new strong password. It clears from the clipboard in 30 seconds.");
    }

    /// <summary>Copies text and wipes it from the clipboard 30 seconds later, unless something else was copied meanwhile.</summary>
    private void CopySecret(string secret)
    {
        Clipboard.SetText(secret);
        _clipboardTimer?.Stop();
        _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clipboardTimer.Tick += (_, _) =>
        {
            _clipboardTimer?.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == secret) Clipboard.Clear(); } catch { }
        };
        _clipboardTimer.Start();
    }

    /// <summary>Opens the login page with the vault's details filled in, then replaces the account's saved session.</summary>
    private async void OnSignInAgain(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account a) return;
        var login = string.IsNullOrWhiteSpace(a.LoginName) ? a.Username : a.LoginName;
        var w = new LoginWindow(prefillUser: login, prefillPass: a.Password) { Owner = this };
        if (w.ShowDialog() != true || w.Cookie == null) return;

        var user = await _api.GetAuthenticatedUserAsync(w.Cookie);
        if (user == null) { SetStatus("That sign-in didn't work."); return; }
        if (user.Id != a.UserId)
        {
            SetStatus($"You signed in as {user.Name}, not {a.Username}, so it was added as a separate account.");
            await AddFromLoginWindowAsync(w);
            return;
        }
        a.Cookie = w.Cookie;
        a.Status = AccountStatus.Valid;
        a.LastValidated = DateTime.Now;
        if (!string.IsNullOrEmpty(w.CapturedPassword)) a.Password = w.CapturedPassword!;
        if (!string.IsNullOrWhiteSpace(w.CapturedLogin)) a.LoginName = w.CapturedLogin!;
        Save();
        SetStatus($"Refreshed {a.Title}'s login.");
    }
}

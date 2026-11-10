using System.Diagnostics;
using System.Windows;
using RobloxAccountManager.Services;

namespace RobloxAccountManager;

/// <summary>Update banner and one-click install from GitHub Releases.</summary>
public partial class MainWindow
{
    private readonly Updater _updater = new();
    private UpdateInfo? _update;

    private async Task CheckForUpdateAsync(bool manual)
    {
        try
        {
            var u = await _updater.CheckAsync();
            if (u == null)
            {
                if (manual) SetStatus($"You're on the latest version ({Updater.CurrentVersion}).");
                return;
            }
            if (!manual && _data.Settings.SkippedVersion == u.Tag) return;
            _update = u;
            UpdateTitle.Text = $"Version {u.Version} is available";
            UpdateSub.Text = $"You have {Updater.CurrentVersion}. Updating takes a few seconds and restarts the app. Roblox windows stay open.";
            UpdateBanner.Visibility = Visibility.Visible;
            _tray?.ShowBalloonTip(3000, "Update available", $"Roblox Account Manager {u.Version} is ready to install.", System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
            if (manual) SetStatus("Couldn't check for updates. Are you online?");
        }
    }

    private void OnCheckUpdates(object sender, RoutedEventArgs e) => _ = CheckForUpdateAsync(manual: true);

    private void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (_update != null) Process.Start(new ProcessStartInfo(_update.PageUrl) { UseShellExecute = true });
    }

    private void OnDismissUpdate(object sender, RoutedEventArgs e)
    {
        if (_update != null) { _data.Settings.SkippedVersion = _update.Tag; Save(); }
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => SetStatus($"Downloading update... {p:P0}"));
            await _updater.InstallAsync(_update, progress);
            // Roblox clients are separate processes, so they keep running. Release the
            // multi-window lock last so the new copy can take it straight over.
            _reallyExit = true;
            SaveSettings();
            Close();
        }
        catch (Exception ex)
        {
            SetStatus($"Update failed: {ex.Message}");
            UpdateButton.IsEnabled = true;
        }
    }
}

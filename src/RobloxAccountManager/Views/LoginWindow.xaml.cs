using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace RobloxAccountManager.Views;

/// <summary>
/// Opens the real roblox.com login page in an isolated, throwaway browser profile and captures
/// the .ROBLOSECURITY session cookie once the user has signed in. The profile is deleted afterwards
/// so each login is separate and nothing lingers on disk outside the encrypted store.
/// </summary>
public partial class LoginWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _profileDir;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private CoreWebView2Environment? _env;

    public string? Cookie { get; private set; }

    private readonly bool _signUp;

    /// <param name="signUp">Open Roblox's sign-up page instead of the login page.</param>
    public LoginWindow(bool signUp = false)
    {
        _signUp = signUp;
        InitializeComponent();
        _profileDir = Path.Combine(Path.GetTempPath(), "RobloxAccountManager-login-" + Guid.NewGuid().ToString("N"));
        Loaded += OnLoaded;
        Closed += OnClosed;
        _poll.Tick += async (_, _) => await CheckCookieAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _env = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileDir);
            await Web.EnsureCoreWebView2Async(_env);
            Web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckCookieAsync();
            if (_signUp)
            {
                Title = "Create a Roblox account";
                Header.Text = "Fill in Roblox's sign-up form as normal. When the account is made, it's added to your list and this window closes.";
            }
            Web.CoreWebView2.Navigate(_signUp ? "https://www.roblox.com/" : "https://www.roblox.com/login");
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            System.Windows.MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is needed for signing in. Install it from Microsoft, or use \"Add with cookie\" instead.",
                "WebView2 missing", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Close();
        }
    }

    private async Task CheckCookieAsync()
    {
        if (Cookie != null || Web.CoreWebView2 == null) return;
        var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
        var sec = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY" && !string.IsNullOrEmpty(c.Value));
        if (sec == null) return;
        Cookie = sec.Value;
        _poll.Stop();
        DialogResult = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _poll.Stop();
        Web.Dispose();
        // The browser process holds files open for a moment after closing; retry the cleanup briefly.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 10 && Directory.Exists(_profileDir); i++)
            {
                try { Directory.Delete(_profileDir, true); }
                catch { await Task.Delay(500); }
            }
        });
    }
}

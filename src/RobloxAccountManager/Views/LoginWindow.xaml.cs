using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

/// <summary>
/// Opens the real roblox.com login or sign-up page in an isolated, throwaway browser profile and captures
/// the .ROBLOSECURITY session cookie once the user is signed in. It also remembers what was typed into the
/// username and password boxes so they can go into the password vault. The profile is deleted afterwards.
/// </summary>
public partial class LoginWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string UserSelector = "#signup-username, #login-username, input[name=\"username\"], input[autocomplete=\"username\"]";
    private const string PassSelector = "#signup-password, #login-password, input[type=\"password\"]";

    // Reports typed values back to the app as the user types (works across page changes).
    private const string CaptureScript = @"(function(){
  function send(){ try {
    var u = document.querySelector('" + "#signup-username, #login-username, input[name=\"username\"], input[autocomplete=\"username\"]" + @"');
    var p = document.querySelector('#signup-password, #login-password, input[type=""password""]');
    window.chrome.webview.postMessage(JSON.stringify({ u: u ? u.value : '', p: p ? p.value : '' }));
  } catch (e) {} }
  document.addEventListener('input', send, true);
  document.addEventListener('change', send, true);
})();";

    private readonly string _profileDir;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool _signUp;
    private readonly string? _prefillUser;
    private readonly string? _prefillPass;
    private bool _prefilled;

    public string? Cookie { get; private set; }
    /// <summary>The username / email / phone typed in, if any.</summary>
    public string? CapturedLogin { get; private set; }
    public string? CapturedPassword { get; private set; }

    /// <param name="signUp">Open Roblox's sign-up page with the username/password generator.</param>
    /// <param name="prefillUser">Fill these into the login form (from the vault).</param>
    public LoginWindow(bool signUp = false, string? prefillUser = null, string? prefillPass = null)
    {
        InitializeComponent();
        _signUp = signUp;
        _prefillUser = prefillUser;
        _prefillPass = prefillPass;
        _profileDir = Path.Combine(Path.GetTempPath(), "RobloxAccountManager-login-" + Guid.NewGuid().ToString("N"));
        Loaded += OnLoaded;
        Closed += OnClosed;
        _poll.Tick += async (_, _) => await CheckCookieAsync();

        if (_signUp)
        {
            Title = Bar.Title = "Create a Roblox account";
            Header.Text = "This is Roblox's own sign-up page. Solve any check it shows you. When the account is made it's added to your list and this window closes.";
            GeneratorPanel.Visibility = Visibility.Visible;
            GenPass.Text = CredentialGenerator.NewPassword();
            _ = NewUserAsync();
        }
        else if (_prefillUser != null)
        {
            Header.Text = "Your saved login from the vault is filled in. Press Log In, and complete any check Roblox shows.";
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileDir);
            await Web.EnsureCoreWebView2Async(env);
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CaptureScript);
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                await CheckCookieAsync();
                if (!_prefilled && _prefillUser != null)
                {
                    await Task.Delay(800); // let the page's scripts render the form
                    _prefilled = await FillAsync(_prefillUser, _prefillPass ?? "");
                }
            };
            Web.CoreWebView2.Navigate(_signUp ? "https://www.roblox.com/" : "https://www.roblox.com/login");
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            System.Windows.MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is needed for signing in. Install it from Microsoft, or use \"Paste a cookie\" instead.",
                "WebView2 missing", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Close();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var u = doc.RootElement.GetProperty("u").GetString();
            var p = doc.RootElement.GetProperty("p").GetString();
            if (!string.IsNullOrWhiteSpace(u)) CapturedLogin = u.Trim();
            if (!string.IsNullOrEmpty(p)) CapturedPassword = p;
        }
        catch { }
    }

    /// <summary>Puts values into the page's username/password boxes the way typing would.</summary>
    private async Task<bool> FillAsync(string user, string pass)
    {
        if (Web.CoreWebView2 == null) return false;
        var script = @"(function(u, p){
  function set(sel, v){ var el = document.querySelector(sel); if (!el || !v) return 0;
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    el.dispatchEvent(new Event('blur', { bubbles: true })); return 1; }
  return set(" + JsonSerializer.Serialize(UserSelector) + @", u) + set(" + JsonSerializer.Serialize(PassSelector) + @", p);
})(" + JsonSerializer.Serialize(user) + ", " + JsonSerializer.Serialize(pass) + ");";
        var result = await Web.CoreWebView2.ExecuteScriptAsync(script);
        return int.TryParse(result, out var n) && n > 0;
    }

    // ---------------------------------------------------------------- generator

    private async Task NewUserAsync()
    {
        UserStatus.Text = "Finding a free username...";
        GenUser.Text = await CredentialGenerator.NewAvailableUsernameAsync();
        await CheckUserAsync();
    }

    private async Task CheckUserAsync()
    {
        var name = GenUser.Text.Trim();
        if (name.Length == 0) { UserStatus.Text = ""; return; }
        var (ok, msg) = await CredentialGenerator.CheckUsernameAsync(name);
        UserStatus.Text = (ok ? "✓ " : "✕ ") + msg;
    }

    private void OnNewUser(object sender, RoutedEventArgs e) => _ = NewUserAsync();
    private void OnUserEdited(object sender, RoutedEventArgs e) => _ = CheckUserAsync();
    private void OnNewPass(object sender, RoutedEventArgs e) => GenPass.Text = CredentialGenerator.NewPassword();

    private void OnCopyPass(object sender, RoutedEventArgs e)
    {
        if (GenPass.Text.Length > 0) Clipboard.SetText(GenPass.Text);
    }

    private async void OnFill(object sender, RoutedEventArgs e)
    {
        CapturedLogin = GenUser.Text.Trim();
        CapturedPassword = GenPass.Text;
        if (!await FillAsync(CapturedLogin, CapturedPassword))
            UserStatus.Text = "Couldn't find the sign-up form on this page. Copy the details in by hand.";
    }

    // ---------------------------------------------------------------- finish

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

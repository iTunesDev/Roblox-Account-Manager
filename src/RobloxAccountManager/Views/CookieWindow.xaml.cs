using System.Windows;

namespace RobloxAccountManager.Views;

public partial class CookieWindow : Wpf.Ui.Controls.FluentWindow
{
    public string Cookie { get; private set; } = "";

    public CookieWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CookieBox.Focus();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var value = CookieBox.Password.Trim();
        const string prefix = ".ROBLOSECURITY=";
        if (value.StartsWith(prefix)) value = value[prefix.Length..];
        value = value.TrimEnd(';');
        if (value.Length < 100)
        {
            System.Windows.MessageBox.Show(this, "That doesn't look like a full .ROBLOSECURITY cookie.", "Add account", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        Cookie = value;
        DialogResult = true;
    }
}

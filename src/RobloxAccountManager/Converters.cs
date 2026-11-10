using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RobloxAccountManager;

/// <summary>Visible when the value is a non-empty string (and not "Any"), or true, or a non-null object.</summary>
public class ShowIfConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var show = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s) && s != "Any",
            int i => i != 0,
            _ => true,
        };
        if (Invert) show = !show;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

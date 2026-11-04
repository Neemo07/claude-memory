using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeMemory.App.Converters;

/// <summary>
/// Shows an element when a boolean is <c>false</c>. Used for the "nothing generated yet"
/// empty state, which is the inverse of <c>HasOutput</c>.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

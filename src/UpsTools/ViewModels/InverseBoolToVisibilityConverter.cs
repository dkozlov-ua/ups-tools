using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UpsTools.ViewModels;

/// <summary>Shows an element while a flag is false, e.g. "no UPS paired" while there is none.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

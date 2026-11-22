using System.Globalization;
using System.Windows.Data;

namespace UpsTools.ViewModels;

/// <summary>A length times the factor in the parameter, e.g. at most 70 % of the height for one part of a page.</summary>
public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double length ? length * double.Parse((string)parameter!, CultureInfo.InvariantCulture) : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// How many columns a UPS's six indicators take in a grid this wide: all six in a row from the width in the parameter
/// on, otherwise two rows of three.
/// </summary>
public sealed class IndicatorColumnsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && width >= double.Parse((string)parameter!, CultureInfo.InvariantCulture) ? 6 : 3;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

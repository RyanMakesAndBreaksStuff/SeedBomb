using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SeedBomb.Converters;

/// <summary>
/// Returns <see cref="Visibility.Collapsed"/> when the value is <c>true</c>,
/// <see cref="Visibility.Visible"/> when <c>false</c>.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Seedbomb.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when value is a non-empty string,
/// <see cref="Visibility.Collapsed"/> when null or empty.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

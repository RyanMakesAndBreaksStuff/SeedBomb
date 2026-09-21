using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SeedBomb.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when value is non-null,
/// <see cref="Visibility.Collapsed"/> when null.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Seedbomb.Converters;

/// <summary>Maps <see langword="true"/> to <see cref="Visibility.Visible"/>, otherwise collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

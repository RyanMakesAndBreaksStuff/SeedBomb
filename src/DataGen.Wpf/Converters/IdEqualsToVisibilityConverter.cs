using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Seedbomb.Converters;

/// <summary>Visible when two bound <see cref="Guid"/> values are equal, otherwise collapsed.</summary>
public sealed class IdEqualsToVisibilityConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [Guid id, Guid connected] && id == connected
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

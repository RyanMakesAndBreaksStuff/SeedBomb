using System.Globalization;
using System.Windows.Data;

namespace SeedBomb.Converters;

/// <summary>
/// True when two multi-binding values are equal.
/// Settings palette tiles use this so a palette change refreshes checks.
/// </summary>
public sealed class EqualityToBoolConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is { Length: >= 2 } && Equals(values[0]?.ToString(), values[1]?.ToString());

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

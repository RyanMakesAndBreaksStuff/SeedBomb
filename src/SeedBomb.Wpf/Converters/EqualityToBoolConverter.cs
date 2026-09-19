using System.Globalization;
using System.Windows.Data;

namespace Seedbomb.Converters;

/// <summary>
/// True when two multi-binding values are equal, or when a single value equals ConverterParameter.
/// Settings palette tiles use the multi-binding form (Id, PaletteId) so a palette change refreshes checks.
/// </summary>
public sealed class EqualityToBoolConverter : IValueConverter, IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is { Length: >= 2 } && Equals(values[0]?.ToString(), values[1]?.ToString());

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
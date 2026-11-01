using System.Globalization;
using System.Windows.Data;

namespace SeedBomb.Converters;

/// <summary>
/// Binds an <see cref="int"/> view-model property to a NumberBox, whose value is a nullable double.
/// A cleared box (null) or an unusable number maps to 0, which the parallelism settings read as "automatic".
/// </summary>
[ValueConversion(typeof(int), typeof(double?))]
public sealed class NullToZeroConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int number ? (double)number : 0d;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double number && double.IsFinite(number) ? (int)Math.Round(number) : 0;
}

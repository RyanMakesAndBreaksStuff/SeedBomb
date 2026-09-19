using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Seedbomb.Converters;

/// <summary>Visible when two bound values are equal, otherwise collapsed. Equality is delegated to
/// <see cref="EqualityToBoolConverter"/> rather than reimplemented here.</summary>
public sealed class IdEqualsToVisibilityConverter : IMultiValueConverter
{
    private static readonly EqualityToBoolConverter Equality = new();

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Equality.Convert(values, typeof(bool), parameter, culture) is true
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
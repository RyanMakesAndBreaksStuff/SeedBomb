using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Seedbomb.Converters;

/// <summary>Visible when the bound step index equals the converter parameter.</summary>
public sealed class StepVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int current)
            return Visibility.Collapsed;

        var target = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => -1,
        };

        return current == target ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Seedbomb.Converters;

/// <summary>Maps TestSucceeded onto DG.Success or DG.Warning.</summary>
public sealed class TestResultBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "DG.Success" : "DG.Warning";
        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

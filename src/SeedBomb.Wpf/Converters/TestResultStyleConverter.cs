using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Seedbomb.Converters;

/// <summary>Maps TestSucceeded onto DG.InfoBanner or DG.WarningBanner.</summary>
public sealed class TestResultStyleConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "DG.InfoBanner" : "DG.WarningBanner";
        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

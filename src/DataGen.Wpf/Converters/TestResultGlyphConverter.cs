using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace Seedbomb.Converters;

/// <summary>Maps TestSucceeded onto a SymbolRegular glyph.</summary>
public sealed class TestResultGlyphConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

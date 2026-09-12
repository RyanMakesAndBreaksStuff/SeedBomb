using System.Globalization;
using System.Windows;
using Seedbomb.Converters;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class ConverterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(2, "2", Visibility.Visible)]
    [InlineData(2, "3", Visibility.Collapsed)]
    [InlineData(0, "0", Visibility.Visible)]
    public void StepVisibility_MatchesParameter(int current, string parameter, Visibility expected)
    {
        var c = new StepVisibilityConverter();
        Assert.Equal(expected, c.Convert(current, typeof(Visibility), parameter, Inv));
    }

    [Fact]
    public void InverseBool_Negates()
    {
        var c = new InverseBoolConverter();
        Assert.Equal(false, c.Convert(true, typeof(bool), null, Inv));
        Assert.Equal(true, c.Convert(false, typeof(bool), null, Inv));
    }

    [Fact]
    public void EqualityToBool_MultiBindingComparesTwoValues()
    {
        var c = new EqualityToBoolConverter();
        Assert.Equal(true, c.Convert(["violet-ink", "violet-ink"], typeof(bool), null, Inv));
        Assert.Equal(false, c.Convert(["violet-ink", "graphite"], typeof(bool), null, Inv));
    }

    [Fact]
    public void NullToVisibility_NonNullVisible()
    {
        var c = new NullToVisibilityConverter();
        Assert.Equal(Visibility.Visible, c.Convert("x", typeof(Visibility), null, Inv));
        Assert.Equal(Visibility.Collapsed, c.Convert(null, typeof(Visibility), null, Inv));
    }
}

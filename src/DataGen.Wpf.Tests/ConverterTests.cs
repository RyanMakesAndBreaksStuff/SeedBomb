using System.Globalization;
using System.Windows;
using Seedbomb.Converters;
using Seedbomb.Services.Connections;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class ConverterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void BoolToVisibility_TrueVisible_FalseCollapsed()
    {
        var c = new BoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible, c.Convert(true, typeof(Visibility), null, Inv));
        Assert.Equal(Visibility.Collapsed, c.Convert(false, typeof(Visibility), null, Inv));
    }

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

    [Theory]
    [InlineData(AuthType.ClientSecret, "ClientSecret", Visibility.Visible)]
    [InlineData(AuthType.OAuth, "ClientSecret", Visibility.Collapsed)]
    [InlineData(AuthType.Certificate, "Certificate", Visibility.Visible)]
    [InlineData(AuthType.Certificate, "ClientSecret", Visibility.Collapsed)]
    public void AuthTypeToVisibility_MatchesParameter(AuthType value, string parameter, Visibility expected)
    {
        var c = new AuthTypeToVisibilityConverter();
        Assert.Equal(expected, c.Convert(value, typeof(Visibility), parameter, Inv));
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

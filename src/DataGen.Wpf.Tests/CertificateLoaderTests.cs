using Seedbomb.Services.Auth;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class CertificateLoaderTests
{
    [Theory]
    [InlineData("A1 B2 C3", "A1B2C3")]
    [InlineData("a1b2c3", "A1B2C3")]
    [InlineData("\u200E A1B2C3 ", "A1B2C3")]
    public void Normalize_StripsSpacesAndLeftToRightMarksAndUppercases(string input, string expected)
        => Assert.Equal(expected, CertificateLoader.Normalize(input));

    [Fact]
    public void Load_Throws_WhenThumbprintIsNotInTheStore()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CertificateLoader.Load("0000000000000000000000000000000000000000"));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

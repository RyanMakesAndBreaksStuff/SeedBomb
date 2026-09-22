using SeedBomb.Services.Connections;
using System.Text.Json;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class LegacyAuthTypeCoercionTests
{
    [Theory]
    [InlineData("\"UserPassword\"", null, AuthType.OAuth)]
    [InlineData("\"userpassword\"", null, AuthType.OAuth)]
    [InlineData("\"Certificate\"", "ABC123", AuthType.Certificate)]
    [InlineData("\"Certificate\"", null, AuthType.Certificate)]
    [InlineData("\"OAuth\"", null, AuthType.OAuth)]
    [InlineData("\"ClientSecret\"", null, AuthType.ClientSecret)]
    [InlineData("\"NotARealType\"", null, AuthType.OAuth)]
    [InlineData("2", null, AuthType.OAuth)]
    [InlineData("2", "ABC123", AuthType.Certificate)]
    [InlineData("3", null, AuthType.OAuth)]
    [InlineData("0", null, AuthType.OAuth)]
    [InlineData("1", null, AuthType.ClientSecret)]
    public void CoerceLegacyAuthType_MapsRopcAndKeepsNamedCertificate(
        string tokenJson, string? thumbprint, AuthType expected)
    {
        using var doc = JsonDocument.Parse(tokenJson);
        Assert.Equal(expected, JsonConnectionProfileService.CoerceLegacyAuthType(doc.RootElement, thumbprint));
    }

    [Fact]
    public void CoerceLegacyAuthJson_RewritesUserPasswordAndLeavesNamedCertificate()
    {
        var json = """
        {
          "Profiles": [
            { "Name": "legacy", "AuthType": "UserPassword" },
            { "Name": "cert", "AuthType": "Certificate", "CertificateThumbprint": "ABC" },
            { "Name": "int-ropc", "AuthType": 2 },
            { "Name": "int-cert", "AuthType": 2, "CertificateThumbprint": "DEF" }
          ]
        }
        """;

        var rewritten = JsonConnectionProfileService.CoerceLegacyAuthJson(json);
        using var doc = JsonDocument.Parse(rewritten);
        var profiles = doc.RootElement.GetProperty("Profiles");

        Assert.Equal("OAuth", profiles[0].GetProperty("AuthType").GetString());
        Assert.Equal("Certificate", profiles[1].GetProperty("AuthType").GetString());
        Assert.Equal("OAuth", profiles[2].GetProperty("AuthType").GetString());
        Assert.Equal("Certificate", profiles[3].GetProperty("AuthType").GetString());
    }
}

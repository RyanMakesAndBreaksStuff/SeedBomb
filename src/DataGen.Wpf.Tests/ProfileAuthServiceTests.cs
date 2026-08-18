using Moq;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class ProfileAuthServiceTests
{
    [Fact]
    public void ShouldDropSessionOnlyWhenActiveProfileMissing()
    {
        var id = Guid.NewGuid();
        Assert.False(ProfileAuthService.ShouldDropSession(id, [id, Guid.NewGuid()]));
        Assert.True(ProfileAuthService.ShouldDropSession(id, [Guid.NewGuid()]));
        Assert.False(ProfileAuthService.ShouldDropSession(null, [id]));
    }

    [Fact]
    public void CreateCachePropertiesUsesDataGenLocalAppData()
    {
        var props = ProfileAuthService.CreateCacheProperties();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataGen");
        Assert.Equal(dir, props.CacheDirectory);
        Assert.Equal("msal_cache.bin", props.CacheFileName);
    }

    [Fact]
    public void AuthTypeValuesDoesNotOfferUserPassword()
    {
        Assert.DoesNotContain("UserPassword", Enum.GetNames<AuthType>());
        Assert.Equal(
            [AuthType.OAuth, AuthType.ClientSecret, AuthType.Certificate],
            AuthTypeValues.All);
    }
}

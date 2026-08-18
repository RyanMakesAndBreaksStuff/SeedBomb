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

    [Fact]
    public void AuthTypeValuesOAuthOnlyIsDrawerQuickCreateList()
    {
        Assert.Equal([AuthType.OAuth], AuthTypeValues.OAuthOnly);
        Assert.DoesNotContain(AuthType.ClientSecret, AuthTypeValues.OAuthOnly);
        Assert.DoesNotContain(AuthType.Certificate, AuthTypeValues.OAuthOnly);
    }

    // Vacuous on empty cache: ProfileAuthService has no public seed for _clients.
    // Asserting HasNoCachedClients after a real sign-in is manual verification only.
    [Fact]
    public async Task SignOutAsync_ClearsCachedClientsAndActiveProfile()
    {
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionProfile>());

        var svc = new ProfileAuthService(profiles.Object);

        await svc.SignOutAsync(TestContext.Current.CancellationToken);

        Assert.Null(svc.CurrentUserDisplayName);
        Assert.True(svc.HasNoCachedClients);
    }
}

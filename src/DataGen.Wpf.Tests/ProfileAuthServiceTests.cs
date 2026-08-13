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
        Assert.DoesNotContain(AuthType.UserPassword, AuthTypeValues.All);
        Assert.Contains(AuthType.OAuth, AuthTypeValues.All);
        Assert.Contains(AuthType.ClientSecret, AuthTypeValues.All);
    }

    [Fact]
    public async Task UserPasswordSignInIsRejected()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var profile = new ConnectionProfile
        {
            AuthType = AuthType.UserPassword,
            EnvironmentUrl = "https://org.crm.dynamics.com",
            ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
            TenantId = "11111111-1111-1111-1111-111111111111",
            Username = "user@contoso.com",
            Password = "secret",
        };
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        profiles.Setup(p => p.SetLastUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var sut = new ProfileAuthService(profiles.Object);
        var result = await sut.SignInAsync(1, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("no longer supported", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}

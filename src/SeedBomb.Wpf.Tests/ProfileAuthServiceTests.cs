using Microsoft.Identity.Client;
using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Diagnostics;
using Xunit;

namespace SeedBomb.Wpf.Tests;

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
    public void CreateUserCachePropertiesUsesSeedBombLocalAppData()
    {
        var props = ProfileAuthService.CreateUserCacheProperties();
        Assert.Equal(AppPaths.Root, props.CacheDirectory);
        Assert.Equal("msal_user_cache.bin", props.CacheFileName);
    }

    [Fact]
    public void CreateAppCachePropertiesUsesSeedBombLocalAppData()
    {
        var props = ProfileAuthService.CreateAppCacheProperties();
        Assert.Equal(AppPaths.Root, props.CacheDirectory);
        Assert.Equal("msal_app_cache.bin", props.CacheFileName);
    }

    [Fact]
    public void UserAndAppCacheFilesAreDistinct()
    {
        Assert.NotEqual(
            ProfileAuthService.CreateUserCacheProperties().CacheFileName,
            ProfileAuthService.CreateAppCacheProperties().CacheFileName);
    }

    [Fact]
    public void AuthTypeValuesDoesNotOfferUserPassword()
    {
        Assert.DoesNotContain("UserPassword", Enum.GetNames<AuthType>());
        Assert.Equal(
            [AuthType.OAuth, AuthType.ClientSecret, AuthType.Certificate],
            AuthTypeValues.All);
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

    [Fact]
    public async Task SignOutAsync_RemovesCachedAccount_ForOAuthProfileWithCachedSession()
    {
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionProfile>());

        var svc = new ProfileAuthService(profiles.Object);

        var account = Mock.Of<IAccount>(a => a.Username == "user@contoso.com");
        var pca = new Mock<IPublicClientApplication>();
        pca.Setup(p => p.GetAccountsAsync()).ReturnsAsync([account]);
        pca.Setup(p => p.RemoveAsync(account)).Returns(Task.CompletedTask);
        svc.CreatePcaOverride = _ => pca.Object;

        var profile = MakeOAuthProfile();
        await svc.GetOrCreatePca(profile, commitSession: true);

        await svc.SignOutAsync(TestContext.Current.CancellationToken);

        pca.Verify(p => p.RemoveAsync(account), Times.Once);
        Assert.True(svc.HasNoCachedClients);
        Assert.Null(svc.CurrentUserDisplayName);
    }

    [Fact]
    public async Task Deleting_the_signed_in_profile_raises_SignedOut()
    {
        // CR-002: the session used to be dropped silently, so the header kept saying "connected".
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionProfile>());
        var svc = new ProfileAuthService(profiles.Object) { ActiveProfile = MakeOAuthProfile() };
        var signedOut = new TaskCompletionSource();
        svc.SignedOut += (_, _) => signedOut.TrySetResult();

        profiles.Raise(p => p.ProfilesChanged += null, EventArgs.Empty);

        await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(svc.ActiveProfile);
    }

    [Fact]
    public async Task OAuth_sign_in_never_borrows_another_profiles_cached_account()
    {
        // WR-005: every OAuth profile shares msal_user_cache.bin, and FirstOrDefault picked whichever
        // cached user came first. A profile may only use the account it signed in with itself.
        var otherUser = Mock.Of<IAccount>(a => a.Username == "someone@tenant-a.com");
        var pca = new Mock<IPublicClientApplication>();
        pca.Setup(p => p.GetAccountsAsync()).ReturnsAsync([otherUser]);
        var svc = new ProfileAuthService(Mock.Of<IConnectionProfileService>()) { CreatePcaOverride = _ => pca.Object };

        var result = await svc.SignInAsync(MakeOAuthProfile(), nint.Zero, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("No cached session. Please sign in.", result.Error);
        pca.Verify(p => p.AcquireTokenSilent(It.IsAny<IEnumerable<string>>(), It.IsAny<IAccount>()), Times.Never);
    }

    private static ConnectionProfile MakeCertificateProfile() => new()
    {
        Name = "Cert Profile",
        EnvironmentUrl = "https://org.crm.dynamics.com",
        ClientId = Guid.NewGuid().ToString(),
        TenantId = Guid.NewGuid().ToString(),
        AuthType = AuthType.Certificate,
        CertificateThumbprint = "AAAA1111BBBB2222",
    };

    private static ConnectionProfile MakeOAuthProfile() => new()
    {
        Name = "OAuth Profile",
        EnvironmentUrl = "https://org.crm.dynamics.com",
        ClientId = Guid.NewGuid().ToString(),
        TenantId = Guid.NewGuid().ToString(),
        AuthType = AuthType.OAuth,
    };

    [Fact]
    public async Task GetOrCreateCca_RebuildsClient_WhenCertificateThumbprintChanges()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var svc = new ProfileAuthService(profiles.Object);
        var buildCount = 0;
        svc.CreateCcaOverride = _ =>
        {
            buildCount++;
            return Mock.Of<IConfidentialClientApplication>();
        };

        var profile = MakeCertificateProfile();

        var first = await svc.GetOrCreateCca(profile, commitSession: true);

        profile.CertificateThumbprint = "CCCC3333DDDD4444";
        var second = await svc.GetOrCreateCca(profile, commitSession: true);

        Assert.NotSame(first, second);
        Assert.Equal(2, buildCount);
    }

    [Fact]
    public async Task GetOrCreateCca_ReusesClient_WhenCredentialUnchanged()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var svc = new ProfileAuthService(profiles.Object);
        var buildCount = 0;
        svc.CreateCcaOverride = _ =>
        {
            buildCount++;
            return Mock.Of<IConfidentialClientApplication>();
        };

        var profile = MakeCertificateProfile();

        var first = await svc.GetOrCreateCca(profile, commitSession: true);
        var second = await svc.GetOrCreateCca(profile, commitSession: true);

        Assert.Same(first, second);
        Assert.Equal(1, buildCount);
    }

    [Fact]
    public async Task GetOrCreateCca_RebuildsClient_WhenClientSecretChanges()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var svc = new ProfileAuthService(profiles.Object);
        var buildCount = 0;
        svc.CreateCcaOverride = _ =>
        {
            buildCount++;
            return Mock.Of<IConfidentialClientApplication>();
        };

        var profile = MakeCertificateProfile();
        profile.AuthType = AuthType.ClientSecret;
        profile.ClientSecret = "secret-1";

        var first = await svc.GetOrCreateCca(profile, commitSession: true);

        profile.ClientSecret = "secret-2";
        var second = await svc.GetOrCreateCca(profile, commitSession: true);

        Assert.NotSame(first, second);
        Assert.Equal(2, buildCount);
    }

    [Fact]
    public async Task GetOrCreateCca_RebuildsClient_WhenAuthTypeChanges()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var svc = new ProfileAuthService(profiles.Object);
        var buildCount = 0;
        svc.CreateCcaOverride = _ =>
        {
            buildCount++;
            return Mock.Of<IConfidentialClientApplication>();
        };

        var profile = MakeCertificateProfile();
        profile.ClientSecret = "secret-1";

        var first = await svc.GetOrCreateCca(profile, commitSession: true);

        profile.AuthType = AuthType.ClientSecret;
        var second = await svc.GetOrCreateCca(profile, commitSession: true);

        Assert.NotSame(first, second);
        Assert.Equal(2, buildCount);
    }

    [Fact]
    public async Task GetOrCreatePca_RebuildsClient_WhenClientIdChanges()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var svc = new ProfileAuthService(profiles.Object);
        var buildCount = 0;
        svc.CreatePcaOverride = _ =>
        {
            buildCount++;
            return Mock.Of<IPublicClientApplication>();
        };

        var profile = MakeOAuthProfile();

        var first = await svc.GetOrCreatePca(profile, commitSession: true);

        profile.ClientId = Guid.NewGuid().ToString();
        var second = await svc.GetOrCreatePca(profile, commitSession: true);

        Assert.NotSame(first, second);
        Assert.Equal(2, buildCount);
    }

    [Fact]
    public async Task GetOrCreatePca_ReusesClient_WhenCredentialUnchanged()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var svc = new ProfileAuthService(profiles.Object);
        var buildCount = 0;
        svc.CreatePcaOverride = _ =>
        {
            buildCount++;
            return Mock.Of<IPublicClientApplication>();
        };

        var profile = MakeOAuthProfile();

        var first = await svc.GetOrCreatePca(profile, commitSession: true);
        var second = await svc.GetOrCreatePca(profile, commitSession: true);

        Assert.Same(first, second);
        Assert.Equal(1, buildCount);
    }

    [Theory]
    [InlineData("https://contoso.crm.dynamics.com", AzureCloudInstance.AzurePublic)]
    [InlineData("https://contoso.crm.microsoftdynamics.us", AzureCloudInstance.AzureUsGovernment)]
    [InlineData("https://contoso.crm.dynamics.cn", AzureCloudInstance.AzureChina)]
    public void ResolveCloud_MapsTheEnvironmentHostSuffix(string url, AzureCloudInstance expected)
    {
        Assert.Equal(expected, ProfileAuthService.ResolveCloud(
            new ConnectionProfile { EnvironmentUrl = url }));
    }

    [Fact]
    public void ResolveCloud_FallsBackToPublicForAnUnrecognisedHost()
    {
        Assert.Equal(AzureCloudInstance.AzurePublic, ProfileAuthService.ResolveCloud(
            new ConnectionProfile { EnvironmentUrl = "not-a-url" }));
    }
}

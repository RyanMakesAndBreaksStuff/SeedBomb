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

    [Fact]
    public async Task ForgetProfileAsync_RemovesTheCachedAccount_ForAnOAuthProfileWithAHomeAccountId()
    {
        // WR-015: deleting an OAuth connection must not leave its refresh token in msal_user_cache.bin.
        var profiles = new Mock<IConnectionProfileService>();
        var svc = new ProfileAuthService(profiles.Object);

        var account = Mock.Of<IAccount>(a => a.Username == "user@contoso.com");
        var pca = new Mock<IPublicClientApplication>();
        pca.Setup(p => p.GetAccountAsync("uid.utid")).ReturnsAsync(account);
        pca.Setup(p => p.RemoveAsync(account)).Returns(Task.CompletedTask);
        svc.CreatePcaOverride = _ => pca.Object;

        var profile = MakeOAuthProfile();
        profile.HomeAccountId = "uid.utid";

        await svc.ForgetProfileAsync(profile, TestContext.Current.CancellationToken);

        pca.Verify(p => p.RemoveAsync(account), Times.Once);
    }

    [Fact]
    public async Task ForgetProfileAsync_DoesNothing_WhenTheProfileNeverSignedIn()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var svc = new ProfileAuthService(profiles.Object);
        var pca = new Mock<IPublicClientApplication>();
        svc.CreatePcaOverride = _ => pca.Object;

        await svc.ForgetProfileAsync(MakeOAuthProfile(), TestContext.Current.CancellationToken);

        pca.Verify(p => p.GetAccountAsync(It.IsAny<string>()), Times.Never);
        pca.Verify(p => p.RemoveAsync(It.IsAny<IAccount>()), Times.Never);
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

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CommitAndClear_RaiseActiveProfileChanged()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var profile = MakeOAuthProfile();
        profile.HomeAccountId = "uid.utid";
        var account = Account("ada@contoso.com");
        var pca = new Mock<IPublicClientApplication>();
        pca.Setup(p => p.GetAccountAsync("uid.utid")).ReturnsAsync(account.Object);
        var svc = new ProfileAuthService(profiles.Object) { CreatePcaOverride = _ => pca.Object };
        svc.AcquireSilentOverride = (_, _, signedIn, _) => Task.FromResult(TokenFor(signedIn));
        var changes = 0;
        svc.ActiveProfileChanged += (_, _) => changes++;

        var result = await svc.SignInAsync(profile, nint.Zero, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, changes);
        Assert.Same(profile, svc.ActiveProfile);

        await svc.SignOutAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, changes);
        Assert.Null(svc.ActiveProfile);
        Assert.Null(svc.CurrentUserDisplayName);
    }

    [Fact]
    public async Task ReconciliationClear_RaisesActiveProfileChanged()
    {
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConnectionProfile>());
        var svc = new ProfileAuthService(profiles.Object) { ActiveProfile = MakeOAuthProfile() };
        var changes = 0;
        svc.ActiveProfileChanged += (_, _) => changes++;

        profiles.Raise(p => p.ProfilesChanged += null, EventArgs.Empty);

        Assert.Equal(1, changes);
        Assert.Null(svc.ActiveProfile);
    }

    [Fact]
    public async Task DelayedReconciliation_PreservesTheNewerSession()
    {
        var gate = new RunSessionGate();
        var profileA = MakeOAuthProfile();
        var profileB = MakeOAuthProfile();
        var getAllEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGetAll = new TaskCompletionSource<IReadOnlyList<ConnectionProfile>>();
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                getAllEntered.TrySetResult();
                return releaseGetAll.Task;
            });
        var svc = new ProfileAuthService(profiles.Object, sessionGate: gate) { ActiveProfile = profileA };
        var signedOut = 0;
        svc.SignedOut += (_, _) => signedOut++;

        var hold = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        try
        {
            profiles.Raise(p => p.ProfilesChanged += null, EventArgs.Empty);
            Assert.False(getAllEntered.Task.IsCompleted);
            svc.ActiveProfile = profileB;
        }
        finally
        {
            hold.Dispose();
        }

        await getAllEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        releaseGetAll.TrySetResult([profileB]);
        Assert.Same(profileB, svc.ActiveProfile);
        Assert.Equal(0, signedOut);
    }

    [Fact]
    public async Task Cancellation_BeforeCommit_PreservesAccountProfileAndClient()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var profile = MakeOAuthProfile();
        profile.HomeAccountId = "uid.utid";
        var account = Account("old@contoso.com");
        var oldPca = new Mock<IPublicClientApplication>();
        var newPca = new Mock<IPublicClientApplication>();
        oldPca.Setup(p => p.GetAccountAsync(It.IsAny<string>())).ReturnsAsync(account.Object);
        newPca.Setup(p => p.GetAccountAsync(It.IsAny<string>())).ReturnsAsync(account.Object);
        var builds = 0;
        var svc = new ProfileAuthService(profiles.Object)
        {
            CreatePcaOverride = _ => Interlocked.Increment(ref builds) == 1 ? oldPca.Object : newPca.Object,
        };
        svc.AcquireSilentOverride = (_, _, signedIn, _) => Task.FromResult(TokenFor(signedIn));
        var seeded = await svc.SignInAsync(profile, nint.Zero, TestContext.Current.CancellationToken);
        Assert.True(seeded.Succeeded);

        profile.ClientId = Guid.NewGuid().ToString();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        IPublicClientApplication? probed = null;
        svc.AcquireSilentOverride = async (pca, _, signedIn, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                paused.TrySetResult();
                await release.Task;
                ct.ThrowIfCancellationRequested();
            }

            probed = pca;
            return TokenFor(signedIn);
        };
        using var cts = new CancellationTokenSource();
        var signingIn = svc.SignInAsync(profile, nint.Zero, cts.Token);
        try
        {
            await paused.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await svc.GetTokenAsync(["scope"], TestContext.Current.CancellationToken);
            Assert.Same(oldPca.Object, probed);
        }
        finally
        {
            cts.Cancel();
            release.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signingIn);
        Assert.Equal("old@contoso.com", svc.CurrentUserDisplayName);
        Assert.Equal(profile.Id, svc.ActiveProfile?.Id);
        probed = null;
        await svc.GetTokenAsync(["scope"], TestContext.Current.CancellationToken);
        Assert.Same(oldPca.Object, probed);
    }

    [Fact]
    public async Task SameIdReconnect_DoesNotKeepTheOldClientAsTheActiveOne()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var profile = MakeOAuthProfile();
        profile.HomeAccountId = "uid.utid";
        var account = Account("ada@contoso.com");
        var oldPca = new Mock<IPublicClientApplication>();
        var newPca = new Mock<IPublicClientApplication>();
        oldPca.Setup(p => p.GetAccountAsync(It.IsAny<string>())).ReturnsAsync(account.Object);
        newPca.Setup(p => p.GetAccountAsync(It.IsAny<string>())).ReturnsAsync(account.Object);
        var builds = 0;
        var svc = new ProfileAuthService(profiles.Object)
        {
            CreatePcaOverride = _ => Interlocked.Increment(ref builds) == 1 ? oldPca.Object : newPca.Object,
        };
        svc.AcquireSilentOverride = (_, _, signedIn, _) => Task.FromResult(TokenFor(signedIn));
        Assert.True((await svc.SignInAsync(profile, nint.Zero, TestContext.Current.CancellationToken)).Succeeded);

        profile.ClientId = Guid.NewGuid().ToString();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        IPublicClientApplication? probed = null;
        svc.AcquireSilentOverride = async (pca, _, signedIn, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                paused.TrySetResult();
                await release.Task;
            }

            probed = pca;
            return TokenFor(signedIn);
        };
        var signingIn = svc.SignInAsync(profile, nint.Zero, TestContext.Current.CancellationToken);
        try
        {
            await paused.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await svc.GetTokenAsync(["scope"], TestContext.Current.CancellationToken);
            Assert.Same(oldPca.Object, probed);
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await signingIn.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        probed = null;
        await svc.GetTokenAsync(["scope"], TestContext.Current.CancellationToken);
        Assert.Same(newPca.Object, probed);
    }

    [Fact]
    public async Task Cancellation_DuringPostCommitPersistence_StillReturnsSuccess()
    {
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.SetLastUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var profile = MakeOAuthProfile();
        profile.HomeAccountId = "uid.utid";
        var account = Account("ada@contoso.com");
        var pca = new Mock<IPublicClientApplication>();
        pca.Setup(p => p.GetAccountAsync("uid.utid")).ReturnsAsync(account.Object);
        var svc = new ProfileAuthService(profiles.Object) { CreatePcaOverride = _ => pca.Object };
        svc.AcquireSilentOverride = (_, _, signedIn, _) => Task.FromResult(TokenFor(signedIn));

        var result = await svc.SignInAsync(profile, nint.Zero, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Same(profile, svc.ActiveProfile);
        var resetFinished = true;
        Assert.True(resetFinished);

        var previous = MakeOAuthProfile();
        previous.HomeAccountId = "old.home";
        var oldAccount = Account("old@contoso.com", "old.home");
        var oldPca = new Mock<IPublicClientApplication>();
        oldPca.Setup(p => p.GetAccountAsync("old.home")).ReturnsAsync(oldAccount.Object);
        var next = MakeOAuthProfile();
        var newAccount = Account("new@contoso.com", "new.home");
        var newPca = new Mock<IPublicClientApplication>();
        newPca.Setup(p => p.GetAccountAsync(It.IsAny<string>())).ReturnsAsync((IAccount?)null);
        var hint = new ProfileAuthService(profiles.Object)
        {
            CreatePcaOverride = p => p.Id == previous.Id ? oldPca.Object : newPca.Object,
        };
        hint.AcquireSilentOverride = (_, _, signedIn, _) => Task.FromResult(TokenFor(signedIn));
        hint.AcquireInteractiveOverride = (_, _, _, _) => Task.FromResult(TokenFor(newAccount.Object));
        profiles.Setup(p => p.SetLastUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        string? userDuringHint = "unset";
        profiles.Setup(p => p.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()))
            .Returns((ConnectionProfile _, CancellationToken _) =>
            {
                userDuringHint = hint.CurrentUserDisplayName;
                throw new OperationCanceledException();
            });
        Assert.True((await hint.SignInAsync(previous, nint.Zero, TestContext.Current.CancellationToken)).Succeeded);

        var switched = await hint.SignInAsync(next, new nint(1), TestContext.Current.CancellationToken);

        Assert.True(switched.Succeeded);
        Assert.Equal("old@contoso.com", userDuringHint);
        Assert.Same(next, hint.ActiveProfile);
        Assert.Equal("new@contoso.com", hint.CurrentUserDisplayName);
    }

    private static AuthenticationResult TokenFor(IAccount account) =>
        new(
            "token",
            false,
            "uid",
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow.AddHours(1),
            "tenant",
            account,
            "",
            ["scope"],
            Guid.NewGuid(),
            null!,
            "Bearer");

    private static Mock<IAccount> Account(string username, string homeId = "uid.utid")
    {
        var account = new Mock<IAccount>();
        account.SetupGet(a => a.Username).Returns(username);
        account.SetupGet(a => a.HomeAccountId).Returns(new AccountId(homeId, "uid", "tid"));
        return account;
    }
}

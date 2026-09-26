using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class DataverseConnectionServiceTests
{
    [Fact]
    public async Task TokenProviderIgnoresCallerCancellationToken()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.GetTokenAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var profiles = new Mock<IConnectionProfileService>();
        using var sut = new DataverseConnectionService(auth.Object, profiles.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var provider = sut.CreateTokenProvider(["https://org.crm.dynamics.com/.default"]);
        var token = await provider("ignored");

        Assert.Equal("token", token);
        auth.Verify(
            a => a.GetTokenAsync(
                It.IsAny<string[]>(),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task ResetAsync_DoesNotBlockWhileAConnectIsInFlight()
    {
        var gate = new TaskCompletionSource();
        var enteredLock = new TaskCompletionSource();
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile).Returns(() =>
        {
            enteredLock.TrySetResult();
            gate.Task.Wait();
            return null; // not signed in: the connect then fails without reaching a live org
        });

        using var svc = new DataverseConnectionService(auth.Object, Mock.Of<IConnectionProfileService>());

        // Occupies the semaphore for the whole of the (blocked) connect.
        var connecting = Task.Run(() => svc.GetOrganizationServiceAsync(CancellationToken.None));
        await enteredLock.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Must return to the caller rather than sit on the thread.
        var resetting = svc.ResetAsync();
        try
        {
            Assert.False(resetting.IsCompleted);
            Assert.NotEqual(resetting, await Task.WhenAny(resetting, Task.Delay(2000, TestContext.Current.CancellationToken)));
        }
        finally
        {
            gate.SetResult();
            try { await connecting; } catch (Exception) { /* no live org to reach */ }
            await resetting;
        }
    }

    [Fact]
    public async Task Connects_only_to_the_signed_in_profile_never_to_last_used()
    {
        // CR-002: last-used can name an org the session never signed in to. The invalid URL makes
        // the old code fail fast at new Uri(...) instead of dialling out.
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionProfile { EnvironmentUrl = "not a signed-in org" });
        using var svc = new DataverseConnectionService(Mock.Of<IAuthService>(), profiles.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetOrganizationServiceAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("Not signed in", ex.Message, StringComparison.Ordinal);
        profiles.Verify(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

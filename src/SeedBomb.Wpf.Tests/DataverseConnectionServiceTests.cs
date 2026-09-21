using Moq;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Dataverse;
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
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                enteredLock.TrySetResult();
                await gate.Task;
                throw new InvalidOperationException("no live org to reach");
            });

        using var svc = new DataverseConnectionService(Mock.Of<IAuthService>(), profiles.Object);

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
}

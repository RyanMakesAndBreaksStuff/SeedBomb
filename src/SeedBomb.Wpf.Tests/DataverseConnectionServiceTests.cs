using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.PowerPlatform.Dataverse.Client;
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

        using var sut = new DataverseConnectionService(auth.Object);
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

        using var svc = new DataverseConnectionService(auth.Object);

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
        // CR-002: the connection comes from the signed-in profile alone (WR-001 removed the
        // service's access to the profile store).
        using var svc = new DataverseConnectionService(Mock.Of<IAuthService>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetOrganizationServiceAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("Not signed in", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceClient_is_built_off_the_calling_thread()
    {
        // WR-006: the constructor signs in and connects synchronously. Built inline, it froze the
        // UI thread on the first Dataverse call after startup or a connection switch.
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile)
            .Returns(new ConnectionProfile { EnvironmentUrl = "https://org.crm.dynamics.com" });
        using var svc = new DataverseConnectionService(auth.Object);
        bool? builtOnPool = null;
        svc.CreateClientOverride = _ =>
        {
            builtOnPool = Thread.CurrentThread.IsThreadPoolThread;
            throw new InvalidOperationException("stop before dialling out");
        };

        // A dedicated thread stands in for the dispatcher: it is not a pool thread.
        var caller = new Thread(() =>
        {
            try { svc.GetOrganizationServiceAsync().GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { }
        });
        caller.Start();
        caller.Join();

        Assert.True(builtOnPool);
    }

    [Fact]
    public async Task Dispose_DoesNotBlockWhileAConnectIsInFlight()
    {
        // WR-001: Dispose waited on the semaphore the connect holds for the whole ServiceClient
        // construction, on the UI thread, during App.OnExit.
        var gate = new TaskCompletionSource();
        var enteredLock = new TaskCompletionSource();
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile).Returns(() =>
        {
            enteredLock.TrySetResult();
            gate.Task.Wait();
            return null; // not signed in: the connect then fails without reaching a live org
        });
        var svc = new DataverseConnectionService(auth.Object);

        var connecting = Task.Run(() => svc.GetOrganizationServiceAsync(CancellationToken.None));
        await enteredLock.Task.WaitAsync(TestContext.Current.CancellationToken);

        var disposing = Task.Run(() => svc.Dispose(), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(disposing, Task.Delay(2000, TestContext.Current.CancellationToken));

        gate.SetResult();
        try { await connecting; } catch (Exception) { /* no live org to reach */ }

        Assert.Same(disposing, finished);
    }

    [Fact]
    public async Task Shutdown_during_a_connect_disposes_the_late_client_once_and_never_publishes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile)
            .Returns(new ConnectionProfile { EnvironmentUrl = "https://org.crm.dynamics.com" });
        var svc = new DataverseConnectionService(auth.Object);
        var building = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        // Only its identity matters, so skip the constructor that dials the org.
        var late = (ServiceClient)RuntimeHelpers.GetUninitializedObject(typeof(ServiceClient));
        GC.SuppressFinalize(late);
        var disposed = new ConcurrentQueue<ServiceClient>();
        svc.CreateClientOverride = _ =>
        {
            building.SetResult();
            gate.Task.Wait();
            return late;
        };
        svc.DisposeClientOverride = disposed.Enqueue;

        var connecting = svc.GetOrganizationServiceAsync(ct);
        await building.Task.WaitAsync(ct);

        var shutdown = svc.ShutdownAsync(); // what Dispose starts
        Assert.False(shutdown.IsCompleted); // queued behind the connect instead of blocking the caller

        gate.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connecting);
        await shutdown; // no SemaphoreFullException or ObjectDisposedException from the lock

        Assert.Same(late, Assert.Single(disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.GetOrganizationServiceAsync(ct));
    }

    [Fact]
    public async Task A_client_that_fails_to_connect_is_disposed_and_never_cached()
    {
        // IN-006: the not-ready client was published to _cached, then silently replaced on retry.
        var ct = TestContext.Current.CancellationToken;
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile)
            .Returns(new ConnectionProfile { EnvironmentUrl = "https://org.crm.dynamics.com" });
        using var svc = new DataverseConnectionService(auth.Object);
        var built = new List<ServiceClient>();
        var disposed = new ConcurrentQueue<ServiceClient>();
        svc.CreateClientOverride = _ =>
        {
            // Uninitialised: IsReady is false, like a client whose sign-in or connect failed.
            var client = (ServiceClient)RuntimeHelpers.GetUninitializedObject(typeof(ServiceClient));
            GC.SuppressFinalize(client);
            built.Add(client);
            return client;
        };
        svc.DisposeClientOverride = disposed.Enqueue;

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetOrganizationServiceAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetOrganizationServiceAsync(ct));

        Assert.Equal(2, built.Count);
        Assert.Equal(built, disposed);
    }
}

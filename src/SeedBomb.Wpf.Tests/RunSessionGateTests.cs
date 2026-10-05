using SeedBomb.Services.Auth;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class RunSessionGateTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Acquire_ExcludesASecondWaiterUntilTheLeaseIsReleased()
    {
        var gate = new RunSessionGate();
        var first = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        var second = gate.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        first.Dispose();
        var lease = await second.WaitAsync(Bound, TestContext.Current.CancellationToken);
        lease.Dispose();
    }

    [Fact]
    public async Task WaitingAcquire_CanBeCancelled_AndTheHolderCanStillRelease()
    {
        var gate = new RunSessionGate();
        var hold = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        hold.Dispose();
        var again = await gate.AcquireAsync(TestContext.Current.CancellationToken)
            .WaitAsync(Bound, TestContext.Current.CancellationToken);
        again.Dispose();
    }

    [Fact]
    public async Task DisposingALeaseTwice_DoesNotAdmitTwoWaiters()
    {
        var gate = new RunSessionGate();
        var lease = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        lease.Dispose();
        lease.Dispose();

        var next = await gate.AcquireAsync(TestContext.Current.CancellationToken)
            .WaitAsync(Bound, TestContext.Current.CancellationToken);
        var blocked = gate.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.False(blocked.IsCompleted);
        next.Dispose();
        (await blocked.WaitAsync(Bound, TestContext.Current.CancellationToken)).Dispose();
    }
}

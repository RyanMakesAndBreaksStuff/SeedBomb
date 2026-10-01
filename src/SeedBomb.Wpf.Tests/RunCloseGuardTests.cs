using Microsoft.Extensions.Logging;
using Moq;
using SeedBomb.Core.Contracts;
using SeedBomb.Core.Metadata;
using SeedBomb.Services.Generation;
using SeedBomb.Services.History;
using SeedBomb.Services.Profiles;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels;
using Wpf.Ui;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class RunCloseGuardTests
{
    [Fact]
    public async Task Close_waits_for_the_history_row_of_a_run_cancelled_by_closing()
    {
        // WR-001: closing mid-run tore the host down under the write, and the run's History write
        // runs after RunViewModel.IsRunning has cleared. A cancel after rows were written
        // returns them (Cancelled = true), so that run must still get its History row.
        var ct = TestContext.Current.CancellationToken;
        var writing = new TaskCompletionSource();
        var generation = Generation(writing, Task.Delay(Timeout.Infinite, ct), whenCancelled: new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
            Cancelled = true,
        });
        var historyStarted = new TaskCompletionSource();
        var historyWrite = new TaskCompletionSource();
        var history = new Mock<IRunHistoryService>();
        history.Setup(h => h.AddRunAsync(It.IsAny<RunRecord>(), It.IsAny<CancellationToken>()))
            .Callback(() => historyStarted.TrySetResult())
            .Returns(historyWrite.Task);
        var generate = NewGenerate(generation, history);
        var closeCalls = 0;
        var closed = new TaskCompletionSource();
        var guard = new RunCloseGuard(generate, () => Task.FromResult(true), () =>
        {
            closeCalls++;
            closed.TrySetResult();
        });

        var run = generate.GenerateCommand.ExecuteAsync(null);
        await writing.Task.WaitAsync(ct);

        Assert.False(guard.AllowClose()); // title-bar close; tray Exit calls the same Window.Close
        await historyStarted.Task.WaitAsync(ct);
        Assert.False(guard.AllowClose()); // a second request while the History write is pending
        Assert.Equal(0, closeCalls);

        historyWrite.SetResult();
        await run;
        await closed.Task.WaitAsync(ct);

        Assert.Equal(1, closeCalls);
        Assert.True(guard.AllowClose()); // the re-issued Close() now goes through
        history.Verify(h => h.AddRunAsync(
            It.Is<RunRecord>(r => !r.Succeeded && r.TotalRecords == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Declining_keeps_the_run_going_and_a_repeated_request_asks_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var writing = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var generate = NewGenerate(Generation(writing, release.Task), new Mock<IRunHistoryService>());
        Action keptOpen = () => Assert.Fail("The window closed although the user kept the run.");
        var run = generate.GenerateCommand.ExecuteAsync(null);
        await writing.Task.WaitAsync(ct);

        // Confirm still open: a second click must not stack a second dialog.
        var pending = new TaskCompletionSource<bool>();
        var askedWhileOpen = 0;
        var open = new RunCloseGuard(generate, () =>
        {
            askedWhileOpen++;
            return pending.Task;
        }, keptOpen);
        Assert.False(open.AllowClose());
        Assert.False(open.AllowClose());
        Assert.Equal(1, askedWhileOpen);
        pending.SetResult(false);

        // Declined (answered synchronously, so no continuation timing is involved).
        var asked = 0;
        var guard = new RunCloseGuard(generate, () =>
        {
            asked++;
            return Task.FromResult(false);
        }, keptOpen);
        Assert.False(guard.AllowClose());
        Assert.False(run.IsCompleted); // declining cancels nothing
        Assert.False(guard.AllowClose()); // the next close asks again
        Assert.Equal(2, asked);

        release.SetResult();
        await run;
        Assert.True(guard.AllowClose()); // nothing in flight any more
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task A_retry_in_flight_is_cancelled_and_awaited_before_closing()
    {
        // Retry runs through RunViewModel alone: it never reaches GenerateAsync or its IsRunning.
        var ct = TestContext.Current.CancellationToken;
        var writing = new TaskCompletionSource();
        var generate = NewGenerate(Generation(writing, Task.Delay(Timeout.Infinite, ct)), new Mock<IRunHistoryService>());
        generate.Run.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
            Elapsed = TimeSpan.FromSeconds(1),
            Errors = [new BatchError("account", 2, "request throttled", -2147015902)],
        }, seed: 40719, environmentHost: "contoso-dev", config: new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 3 },
            Seed = 40719,
        });
        var closed = new TaskCompletionSource();
        var guard = new RunCloseGuard(generate, () => Task.FromResult(true), () => closed.TrySetResult());

        var retry = generate.Run.RetrySelectedCommand.ExecuteAsync(null);
        await writing.Task.WaitAsync(ct);

        Assert.False(guard.AllowClose());
        await closed.Task.WaitAsync(ct);
        Assert.True(retry.IsCompleted);
    }

    // Signals `writing` once the run reaches Dataverse, then finishes when `release` completes or
    // the run is cancelled. `whenCancelled` models BulkCreator after it has written rows; null
    // models a cancel before any write, which throws.
    private static Mock<IWpfGenerationService> Generation(
        TaskCompletionSource writing, Task release, GenerationResult? whenCancelled = null)
    {
        var generation = new Mock<IWpfGenerationService>();
        generation.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(async (GenerationConfig _, IProgress<ProgressUpdate> _, CancellationToken token) =>
            {
                writing.TrySetResult();
                await Task.WhenAny(release, Task.Delay(Timeout.Infinite, token));
                if (!token.IsCancellationRequested)
                    return new GenerationResult { CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>() };
                return whenCancelled ?? throw new OperationCanceledException(token);
            });
        return generation;
    }

    private static GenerateViewModel NewGenerate(
        Mock<IWpfGenerationService> generation, Mock<IRunHistoryService> history)
    {
        var generate = new GenerateViewModel(
            Mock.Of<ISettingsService>(),
            Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(),
            Mock.Of<IMetadataProvider>(),
            Mock.Of<IProfileService>(),
            Mock.Of<IContentDialogService>(),
            new RunViewModel(generation.Object, history: history.Object));
        generate.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        return generate;
    }
}

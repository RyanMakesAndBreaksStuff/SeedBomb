using SeedBomb.Core.Contracts;
using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Generation;
using SeedBomb.Services.History;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

[Collection("RunSession")]
public sealed class RunViewModelTests : IDisposable
{
    // Retry notifications go through RunViewModel.UiDispatcher, else Application.Current.Dispatcher. A
    // StaUi test may own a live, non-pumping Application at the same time, so this collection pins the
    // caller's own dispatcher: notifications run inline and deterministically.
    public RunViewModelTests() => UseInlineRetryNotifications();

    public void Dispose() => RunViewModel.UiDispatcher = null;

    internal static void UseInlineRetryNotifications() => RunViewModel.UiDispatcher = () => Dispatcher.CurrentDispatcher;

    [Theory]
    [InlineData(-2147015902)] // Number of requests exceeded the limit
    [InlineData(-2147015903)] // Combined execution time exceeded the limit
    [InlineData(-2147015898)] // Number of concurrent requests exceeded the limit
    public void Classifier_ServiceProtectionFaultsAreRetryable(int faultCode) =>
        Assert.True(RejectionClassifier.IsRetryable(new BatchError(
            "account", 0, "Number of requests exceeded the limit of 6000 over time window of 300 seconds.", faultCode)));

    [Fact]
    public void Classifier_PluginFailureAndDuplicateAreNotRetryable()
    {
        // 0x80040224 IsvUnExpected: an unexpected error from plugin code needs a fix, not a retry.
        Assert.False(RejectionClassifier.IsRetryable(new BatchError(
            "account", 0, "An unexpected error occurred from ISV code.", -2147220956)));
        Assert.False(RejectionClassifier.IsRetryable(new BatchError("account", 0, "Duplicate key on emailaddress1", null)));
    }

    [Fact]
    public void Classifier_TransientNetworkFailureIsRetryable() =>
        Assert.True(RejectionClassifier.IsRetryable(new BatchError(
            "contact", 0, "Batch creation failed for 'contact' on attempt 1: There was no endpoint listening at …",
            null, RowCount: 50, IsTransient: true)));

    [Fact]
    public void ApplyResult_GroupsByCause_AndDoesNotSelectFixFirst()
    {
        var vm = new RunViewModel();
        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [Guid.NewGuid(), Guid.NewGuid()],
            },
            Elapsed = TimeSpan.FromSeconds(10),
            Errors =
            [
                new BatchError("account", 0, "Duplicate key on emailaddress1", null),
                new BatchError("account", 1, "Duplicate key on emailaddress1", null),
                new BatchError("account", 2, "request throttled", -2147015902),
            ],
        }, seed: 40719, environmentHost: "contoso-dev");

        Assert.Contains("Nothing was rolled back", vm.OutcomeDetail, StringComparison.Ordinal);
        Assert.Equal(2, vm.RejectionGroups.Count);
        var dup = Assert.Single(vm.RejectionGroups, g => !g.IsRetryable);
        Assert.False(dup.IsSelectedForRetry);
        Assert.False(dup.IsRetryable);
        var throttle = Assert.Single(vm.RejectionGroups, g => g.IsRetryable);
        Assert.True(throttle.IsSelectedForRetry);
        Assert.Contains("1 selected", vm.RetryButtonLabel, StringComparison.Ordinal);
        Assert.Equal("FixFirst", dup.DispositionKey);
        Assert.Equal("Retryable", throttle.DispositionKey);
    }

    [Fact]
    public async Task RetrySelected_CallsGenerationService_WithoutGenerateViewModel()
    {
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(),
                It.IsAny<IProgress<ProgressUpdate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
                Elapsed = TimeSpan.FromSeconds(1),
                Errors = [],
            });

        var vm = new RunViewModel(generation: gen.Object);
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 3 },
            Seed = 40719,
        };
        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
            Elapsed = TimeSpan.FromSeconds(1),
            Errors = [new BatchError("account", 2, "request throttled", -2147015902)],
        }, seed: 40719, environmentHost: "contoso-dev", config: config);

        await vm.RetrySelectedCommand.ExecuteAsync(null);

        gen.Verify(g => g.GenerateAsync(
            It.Is<GenerationConfig>(c => c.EntityLogicalNames.Contains("account")),
            It.IsAny<IProgress<ProgressUpdate>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RiskyBogusDeclined_MakesZeroGenerateCalls()
    {
        var gen = new Mock<IWpfGenerationService>();
        var vm = new RunViewModel(generation: gen.Object)
        {
            ConfirmRiskyBogus = () => Task.FromResult(false),
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 3 },
            Seed = 7,
            FieldRules = new Dictionary<string, Dictionary<string, SeedBomb.Core.Rules.FieldRule>>
            {
                ["account"] = new() { ["emailaddress1"] = new SeedBomb.Core.Rules.BogusRule("INTERNET", "email", 1) },
            },
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vm.ExecuteAsync(config, TestContext.Current.CancellationToken));

        gen.Verify(g => g.GenerateAsync(
            It.IsAny<GenerationConfig>(),
            It.IsAny<IProgress<ProgressUpdate>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_RePromptsRiskyBogus_AndCancelledMakesZeroCalls()
    {
        var gen = new Mock<IWpfGenerationService>();
        var prompted = 0;
        var vm = new RunViewModel(generation: gen.Object)
        {
            ConfirmRiskyBogus = () =>
            {
                prompted++;
                return Task.FromResult(false);
            },
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 3 },
            Seed = 7,
            AllowRiskyBogusValues = true,
            FieldRules = new Dictionary<string, Dictionary<string, SeedBomb.Core.Rules.FieldRule>>
            {
                ["account"] = new() { ["emailaddress1"] = new SeedBomb.Core.Rules.BogusRule("INTERNET", "email", 1) },
            },
        };
        vm.ApplyResult(new GenerationResult
        {
            Errors = [new BatchError("account", 2, "request throttled", -2147015902)],
        }, seed: 7, environmentHost: "contoso-dev", config: config);

        // Retry swallows cancellation and reports via ReportRunFailure — the bound command must not throw.
        await vm.RetrySelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, prompted);
        Assert.False(vm.IsRunning);
        gen.Verify(g => g.GenerateAsync(
            It.IsAny<GenerationConfig>(),
            It.IsAny<IProgress<ProgressUpdate>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void StartRun_ClearsActivityFromThePreviousRun()
    {
        var vm = new RunViewModel();

        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 10, tables: ["account"]);
        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [Guid.NewGuid()],
            },
            Elapsed = TimeSpan.FromSeconds(1),
            Errors = [],
        }, seed: 1, environmentHost: "contoso-dev");
        Assert.NotEmpty(vm.RecentActivity);

        vm.StartRun("contoso-dev", seed: 2, plannedTotal: 10, tables: ["account"]);

        Assert.Empty(vm.RecentActivity);
    }

    [Fact]
    public async Task RetrySelected_WhenGenerationThrows_ReportsAndDoesNotPropagate()
    {
        var gen = new Mock<IWpfGenerationService>();
        gen.SetupSequence(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(),
                It.IsAny<IProgress<ProgressUpdate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
                Elapsed = TimeSpan.FromSeconds(1),
                Errors = [new BatchError("account", 0, "request throttled", -2147015902)],
            })
            .ThrowsAsync(new InvalidOperationException("ServiceClient failed to connect"));

        var vm = new RunViewModel(generation: gen.Object);
        await vm.ExecuteAsync(
            new GenerationConfig
            {
                EntityLogicalNames = ["account"],
                RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
            },
            TestContext.Current.CancellationToken);

        // Must not throw: the summary page's primary button is bound straight to this command.
        await vm.RetrySelectedCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void ExportRejectedCsv_WritesToOverrideDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-rejected", Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new RunViewModel { ExportDirectoryOverride = root };
            vm.ApplyResult(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
                Elapsed = TimeSpan.FromSeconds(1),
                Errors = [new BatchError("account", 0, "Duplicate key on emailaddress1", null)],
            }, seed: 1, environmentHost: "contoso-dev");

            vm.ExportRejectedCsvCommand.Execute(null);

            var written = Assert.Single(Directory.GetFiles(root, "seedbomb-rejected-*.csv"));
            Assert.Contains("Duplicate key on emailaddress1", File.ReadAllText(written), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ExportRejectedCsv_EscapesLeadingFormulaCharacterInCause()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-rejected", Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new RunViewModel { ExportDirectoryOverride = root };
            vm.ApplyResult(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
                Elapsed = TimeSpan.FromSeconds(1),
                Errors = [new BatchError("account", 0, "=cmd|'/c calc'!A1", null)],
            }, seed: 1, environmentHost: "contoso-dev");

            vm.ExportRejectedCsvCommand.Execute(null);

            var written = Assert.Single(Directory.GetFiles(root, "seedbomb-rejected-*.csv"));
            var line = File.ReadAllLines(written)[1];
            Assert.Contains("'=cmd", line, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
    [Fact]
    public void ExportRejectedCsv_WhenDirectoryIsUnusable_DoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-rejected", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocked");
        File.WriteAllText(blocker, "");
        try
        {
            var vm = new RunViewModel { ExportDirectoryOverride = blocker };
            vm.ExportRejectedCsvCommand.Execute(null);   // must not throw
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ApplyResult_SetsLastRunSucceeded_FromRejectionCount()
    {
        var clean = new RunViewModel();
        clean.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
            Elapsed = TimeSpan.FromSeconds(1),
            Errors = [],
        }, seed: 1, environmentHost: "contoso-dev");
        Assert.True(clean.LastRunSucceeded);

        var rejected = new RunViewModel();
        rejected.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
            Elapsed = TimeSpan.FromSeconds(1),
            Errors = [new BatchError("account", 0, "Duplicate key on emailaddress1", null)],
        }, seed: 1, environmentHost: "contoso-dev");
        Assert.False(rejected.LastRunSucceeded);
    }

    [Fact]
    public async Task ExecuteAsync_WithBlankHost_ResolvesTheConnectedEnvironmentHost()
    {
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(),
                It.IsAny<IProgress<ProgressUpdate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
                Elapsed = TimeSpan.FromSeconds(1),
                Errors = [],
            });

        // CR-002: the host comes from the live session, never from last-used.
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile)
            .Returns(new ConnectionProfile { EnvironmentUrl = "https://contoso-uat.crm.dynamics.com" });

        var vm = new RunViewModel(generation: gen.Object, auth: auth.Object);
        await vm.ExecuteAsync(
            new GenerationConfig
            {
                EntityLogicalNames = ["account"],
                RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("contoso-uat.crm.dynamics.com", vm.RunDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("written to Dataverse", vm.RunDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void StartRun_DoesNotPublishARejectedMetricTile()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 10, ["account"]);

        Assert.Equal(3, vm.Metrics.Count);
        Assert.DoesNotContain(vm.Metrics, m => m.Label == "Rejected");
    }

    [Fact]
    public void AcceptProgress_OutOfOrderSnapshots_NeverMoveProgressBackwards()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 100, tables: ["account"]);

        vm.AcceptProgress(Update("account", created: 80, total: 100), ["account"], 100);
        // A slower batch's snapshot lands after a faster one's: it carries a lower cumulative count.
        vm.AcceptProgress(Update("account", created: 40, total: 100), ["account"], 100);

        Assert.Equal(80, vm.OverallPercent);
        Assert.Equal("80", vm.OverallPercentLabel);
        Assert.Equal(80, Assert.Single(vm.Tables).Percent);
    }

    [Fact]
    public void ApplyResult_SettlesEveryTableRow_EvenWhenTheLastSnapshotWasDropped()
    {
        var vm = new RunViewModel();
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 100 },
            Seed = 1,
        };
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 100, tables: ["account"]);
        vm.AcceptProgress(Update("account", created: 80, total: 100), ["account"], 100);

        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [.. Enumerable.Range(0, 100).Select(_ => Guid.NewGuid())],
            },
            Elapsed = TimeSpan.FromSeconds(5),
            Errors = [],
        }, seed: 1, environmentHost: "contoso-dev", config: config);

        var row = Assert.Single(vm.Tables);
        Assert.Equal("Done", row.StateKey);
        Assert.Equal("100 / 100", row.ProgressLabel);
        Assert.Equal(100, row.Percent);
        Assert.Equal(100, vm.OverallPercent);
    }

    [Fact]
    public void AcceptProgress_PhaseOnlySnapshot_LogsNoZeroOfZeroCounts()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 100, tables: ["account"]);

        vm.AcceptProgress(Update("", created: 0, total: 0, phase: "Resolving dependencies"), ["account"], 100);

        Assert.Equal("Resolving dependencies", Assert.Single(vm.RecentActivity).Line);
    }

    private static ProgressUpdate Update(
        string entity, int created, int total, string phase = "Generating") =>
        new(phase, entity, created, total, BatchesCompleted: 1, TotalBatches: 4,
            RecordsPerMinute: 0, Elapsed: TimeSpan.FromSeconds(1));

    [Fact]
    public void ApplyResult_RejectionCounts_AreRowsNotBatches()
    {
        var vm = new RunViewModel();
        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [Guid.NewGuid()],
            },
            Elapsed = TimeSpan.FromMinutes(1),
            Errors = [new BatchError("account", 15, "request throttled", -2147015902, 500)],
        }, seed: 42, environmentHost: "contoso-dev");

        Assert.Equal("500", Assert.Single(vm.SummaryStats, s => s.Label == "Rejected").Value);
        Assert.Equal(500, Assert.Single(vm.RejectionGroups).RowCount);
        Assert.Contains("500 rejected rows", vm.OutcomeHeadline, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowHistorical_ShowsSavedActivity()
    {
        var vm = new RunViewModel();
        var run = new SeedBomb.Services.History.RunRecord(Guid.NewGuid(), DateTimeOffset.Now, ["account"], 10,
            TimeSpan.FromSeconds(3), true, 0, ActivityLog: ["Generating  account  10/10", "Finished — 10 written, 0 rejected"]);

        vm.ShowHistorical(run);
        Assert.Equal(run.ActivityLog, vm.SummaryView.ActivityLines);

        vm.ShowHistorical(run with { ActivityLog = null });
        Assert.Empty(vm.SummaryView.ActivityLines);
    }

    [Fact]
    public async Task Cancel_WhenTheDialogHostFails_StillCancelsTheRun()
    {
        // IN-007: the catch returned silently, so Cancel did nothing at all.
        var dialogs = new Mock<IContentDialogService>();
        dialogs.Setup(d => d.ShowAsync(It.IsAny<ContentDialog>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No dialog host"));
        var vm = new RunViewModel(contentDialogService: dialogs.Object) { IsRunning = true };

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.Equal("Cancelling…", vm.StatusHeadline);
    }

    [Fact]
    public void StartRun_AfterViewingAHistoricalRun_NotifiesSummaryView()
    {
        // IN-008: re-navigating to the summary page already on screen is a no-op in WPF-UI, so
        // the page must learn about the swap back to the live run from this notification.
        var vm = new RunViewModel();
        vm.ShowHistorical(new RunRecord(Guid.NewGuid(), DateTimeOffset.Now, ["contact"], 3,
            TimeSpan.FromSeconds(2), true, 0));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.StartRun("contoso-dev", 42, 1, ["account"]);

        Assert.Same(vm, vm.SummaryView);
        Assert.Contains(nameof(RunViewModel.SummaryView), raised);
    }

    [Fact]
    public async Task ExecuteAsync_WhenHistoricalRunIsOpenedDuringGeneration_ShowsLiveRunOnCompletion()
    {
        var completed = new TaskCompletionSource<GenerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = new Mock<IWpfGenerationService>();
        generation.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(() => completed.Task);
        var vm = new RunViewModel(generation: generation.Object) { KeepWindowOpen = false };

        var running = vm.ExecuteAsync(
            new GenerationConfig
            {
                EntityLogicalNames = ["account"],
                RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
                Seed = 42,
            },
            TestContext.Current.CancellationToken);
        vm.ShowHistorical(new SeedBomb.Services.History.RunRecord(Guid.NewGuid(), DateTimeOffset.Now,
            ["contact"], 3, TimeSpan.FromSeconds(2), true, 0));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        completed.SetResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
            Elapsed = TimeSpan.FromSeconds(1),
        });
        await running;

        Assert.False(vm.IsSheetVisible); // Keep Window Open is off.
        Assert.Same(vm, vm.SummaryView);
        Assert.Contains(nameof(RunViewModel.SummaryView), raised);
    }

    [Fact]
    public async Task RetrySelected_AddsAHistoryRow_WithTheFirstRunsLabels()
    {
        // IN-009: retries wrote to Dataverse but never reached GenerateViewModel's History write.
        var gen = new Mock<IWpfGenerationService>();
        gen.SetupSequence(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
                Elapsed = TimeSpan.FromSeconds(1),
                Errors = [new BatchError("account", 0, "request throttled", -2147015902, 2)],
            })
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid(), Guid.NewGuid()] },
                Elapsed = TimeSpan.FromSeconds(1),
            });
        var history = new Mock<IRunHistoryService>();
        var vm = new RunViewModel(generation: gen.Object, history: history.Object);
        await vm.ExecuteAsync(
            new GenerationConfig
            {
                EntityLogicalNames = ["account"],
                RecordCounts = new Dictionary<string, int> { ["account"] = 2 },
            },
            TestContext.Current.CancellationToken,
            tableLabels: new Dictionary<string, string> { ["account"] = "Account" }, profileName: "sales");

        await vm.RetrySelectedCommand.ExecuteAsync(null);

        history.Verify(h => h.AddRunAsync(
            It.Is<RunRecord>(r => r.Id == vm.CurrentRunId && r.Succeeded && r.TotalRecords == 2
                && r.EntityNames.Single() == "Account" && r.Profile == "sales"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Run_UsesProfileActiveAfterPreparation()
    {
        var profileA = Profile("A", "https://a.crm.dynamics.com");
        var profileB = Profile("B", "https://b.crm.dynamics.com");
        var auth = new MutableAuth { ActiveProfile = profileA, CurrentUserDisplayName = "ada@a" };
        var prepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                prepEntered.TrySetResult();
                return releasePrep.Task.ContinueWith(_ => AppSettings.Default);
            });
        var calls = 0;
        var gen = Generation(onCall: () => Interlocked.Increment(ref calls), Throttled());
        RunRecord? recorded = null;
        var history = new Mock<IRunHistoryService>();
        history.Setup(h => h.AddRunAsync(It.IsAny<RunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<RunRecord, CancellationToken>((row, _) => recorded = row)
            .Returns(Task.CompletedTask);
        var vm = new RunViewModel(
            generation: gen.Object, settings: settings.Object, auth: auth, history: history.Object,
            sessionGate: new RunSessionGate());

        var running = vm.ExecuteAsync(AccountConfig(), TestContext.Current.CancellationToken);
        try
        {
            await prepEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            auth.ActiveProfile = profileB;
            auth.CurrentUserDisplayName = "ada@b";
            auth.RaiseChanged();
        }
        finally
        {
            releasePrep.TrySetResult();
        }

        await running.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal("b.crm.dynamics.com", vm.EnvironmentLabel);
        Assert.Equal("ada@b", recorded?.User);
        Assert.Equal(profileB.Id, auth.ActiveProfile?.Id);
        Assert.True(vm.RetrySelectedCommand.CanExecute(null));

        auth.ActiveProfile = profileA;
        auth.RaiseChanged();
        Assert.False(vm.RetrySelectedCommand.CanExecute(null));
        await vm.RetrySelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
        Assert.Equal("b.crm.dynamics.com", vm.EnvironmentLabel);
    }

    [Fact]
    public async Task Retry_RechecksTargetAfterPreparation()
    {
        var profileA = Profile("A", "https://a.crm.dynamics.com");
        var profileB = Profile("B", "https://b.crm.dynamics.com");
        var auth = new MutableAuth { ActiveProfile = profileA, CurrentUserDisplayName = "ada@a" };
        var loads = 0;
        var prepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref loads) == 1)
                    return Task.FromResult(AppSettings.Default);
                prepEntered.TrySetResult();
                return releasePrep.Task.ContinueWith(_ => AppSettings.Default);
            });
        var calls = 0;
        var gen = Generation(onCall: () => Interlocked.Increment(ref calls), Throttled());
        var vm = new RunViewModel(
            generation: gen.Object, settings: settings.Object, auth: auth, sessionGate: new RunSessionGate());
        await vm.ExecuteAsync(AccountConfig(), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal("a.crm.dynamics.com", vm.EnvironmentLabel);

        var retrying = vm.RetrySelectedCommand.ExecuteAsync(null);
        try
        {
            await prepEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            auth.ActiveProfile = profileB;
            auth.CurrentUserDisplayName = "ada@b";
            auth.RaiseChanged();
        }
        finally
        {
            releasePrep.TrySetResult();
        }

        await retrying.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal("a.crm.dynamics.com", vm.EnvironmentLabel);
        Assert.False(vm.RetrySelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProfileChange_NotifiesRetryImmediately()
    {
        var profileA = Profile("A", "https://a.crm.dynamics.com");
        var profileB = Profile("B", "https://b.crm.dynamics.com");
        var auth = new MutableAuth { ActiveProfile = profileA, CurrentUserDisplayName = "ada" };
        var vm = new RunViewModel(generation: Generation(result: Throttled()).Object, auth: auth);
        await vm.ExecuteAsync(AccountConfig(), TestContext.Current.CancellationToken);
        var selected = Assert.Single(vm.RejectionGroups, g => g.IsRetryable).IsSelectedForRetry;
        Assert.True(vm.RetrySelectedCommand.CanExecute(null));

        var notifications = 0;
        vm.RetrySelectedCommand.CanExecuteChanged += (_, _) => notifications++;
        auth.ActiveProfile = profileB;
        auth.RaiseChanged();

        Assert.True(notifications > 0);
        Assert.False(vm.RetrySelectedCommand.CanExecute(null));
        Assert.Equal(selected, Assert.Single(vm.RejectionGroups, g => g.IsRetryable).IsSelectedForRetry);

        var beforeSignOut = notifications;
        auth.ActiveProfile = null;
        auth.CurrentUserDisplayName = null;
        auth.RaiseChanged();
        Assert.True(notifications > beforeSignOut);
        Assert.False(vm.RetrySelectedCommand.CanExecute(null));

        if (string.Equals(Environment.GetEnvironmentVariable("SEEDBOMB_SKIP_GATE_UI"), "1", StringComparison.Ordinal))
            return;
        var notifiedOn = -1;
        var uiThread = 0;
        DispatcherFrame? pumping = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sta = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            pumping = new DispatcherFrame();
            uiThread = Environment.CurrentManagedThreadId;
            RunViewModel.UiDispatcher = () => dispatcher;
            started.TrySetResult();
            Dispatcher.PushFrame(pumping);
            dispatcher.InvokeShutdown();
        });
        sta.IsBackground = true;
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        try
        {
            await started.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            vm.RetrySelectedCommand.CanExecuteChanged += (_, _) =>
            {
                notifiedOn = Environment.CurrentManagedThreadId;
                if (pumping is not null)
                    pumping.Continue = false;
            };
            auth.ActiveProfile = profileA;
            ThreadPool.QueueUserWorkItem(_ => auth.RaiseChanged());
            Assert.True(sta.Join(Bound));
            Assert.Equal(uiThread, notifiedOn);
            Assert.True(vm.RetrySelectedCommand.CanExecute(null));
        }
        finally
        {
            UseInlineRetryNotifications();
            if (pumping is not null)
                pumping.Continue = false;
        }
    }

    [Fact]
    public async Task CancelledSessionWait_DoesNotStartRun()
    {
        var gate = new RunSessionGate();
        var hold = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        var calls = 0;
        var releaseGen = new TaskCompletionSource<GenerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns((GenerationConfig _, IProgress<ProgressUpdate> _, CancellationToken token) =>
            {
                Interlocked.Increment(ref calls);
                token.Register(() => releaseGen.TrySetCanceled(token));
                return releaseGen.Task;
            });
        var vm = new RunViewModel(generation: gen.Object, sessionGate: gate);
        using var cts = new CancellationTokenSource();
        var running = vm.ExecuteAsync(AccountConfig(), cts.Token);
        try
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.False(vm.IsRunning);
            Assert.Equal(0, calls);
        }
        finally
        {
            releaseGen.TrySetResult(Throttled());
            hold.Dispose();
            try
            {
                await running.WaitAsync(Bound, TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException) { }
        }

        using (var again = await gate.AcquireAsync(TestContext.Current.CancellationToken)
                   .WaitAsync(Bound, TestContext.Current.CancellationToken))
            Assert.NotNull(again);

        var exploding = new Mock<IWpfGenerationService>();
        exploding.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ServiceClient failed to connect"));
        var failed = new RunViewModel(generation: exploding.Object, sessionGate: gate);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failed.ExecuteAsync(AccountConfig(), TestContext.Current.CancellationToken));
        using (var afterException = await gate.AcquireAsync(TestContext.Current.CancellationToken)
                   .WaitAsync(Bound, TestContext.Current.CancellationToken))
            Assert.NotNull(afterException);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new Mock<IWpfGenerationService>();
        cancelling.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(async (GenerationConfig _, IProgress<ProgressUpdate> _, CancellationToken token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Throttled();
            });
        var cancelledRun = new RunViewModel(generation: cancelling.Object, sessionGate: gate);
        using var runCts = new CancellationTokenSource();
        var inFlight = cancelledRun.ExecuteAsync(AccountConfig(), runCts.Token);
        await entered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        runCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        using var afterCancel = await gate.AcquireAsync(TestContext.Current.CancellationToken)
            .WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.NotNull(afterCancel);
    }

    [Fact]
    public async Task Retry_IsBlocked_AfterTheSessionMovesToAnotherProfile()
    {
        var dev = Profile("Dev", "https://dev.crm.dynamics.com");
        var prod = Profile("Prod", "https://prod.crm.dynamics.com");
        var auth = new MutableAuth { ActiveProfile = dev, CurrentUserDisplayName = "ada" };
        var calls = 0;
        var gen = Generation(onCall: () => Interlocked.Increment(ref calls), Throttled());
        RunRecord? recorded = null;
        var history = new Mock<IRunHistoryService>();
        history.Setup(h => h.AddRunAsync(It.IsAny<RunRecord>(), It.IsAny<CancellationToken>()))
            .Callback<RunRecord, CancellationToken>((row, _) => recorded = row)
            .Returns(Task.CompletedTask);
        var vm = new RunViewModel(generation: gen.Object, auth: auth, history: history.Object);

        await vm.ExecuteAsync(AccountConfig(), TestContext.Current.CancellationToken);

        Assert.Equal("dev.crm.dynamics.com", vm.EnvironmentLabel);
        Assert.Equal("dev.crm.dynamics.com", recorded?.Environment);
        auth.ActiveProfile = prod;
        auth.RaiseChanged();
        Assert.False(vm.RetrySelectedCommand.CanExecute(null));
        await vm.RetrySelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
    }

    private static GenerationConfig AccountConfig() => new()
    {
        EntityLogicalNames = ["account"],
        RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
        Seed = 42,
    };

    private static GenerationResult Throttled() => new()
    {
        CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
        Elapsed = TimeSpan.FromSeconds(1),
        Errors = [new BatchError("account", 0, "request throttled", -2147015902, 1)],
    };

    private static Mock<IWpfGenerationService> Generation(Action? onCall = null, GenerationResult? result = null)
    {
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                onCall?.Invoke();
                return Task.FromResult(result ?? Throttled());
            });
        return gen;
    }

    private static ConnectionProfile Profile(string name, string url) => new()
    {
        Name = name,
        EnvironmentUrl = url,
        ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
    };

    [Fact]
    public async Task FailedRun_LeavesATerminalFailedSheet()
    {
        // WR-006: the held-open sheet kept "Generating…" and a spinning ring after a pre-write failure.
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ServiceClient failed to connect"));
        var vm = new RunViewModel(generation: gen.Object);
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.ExecuteAsync(config, TestContext.Current.CancellationToken));
        vm.ReportRunFailure(ex);

        Assert.Equal("Failed", vm.StatusHeadline);
        Assert.False(vm.IsIndeterminate);
        Assert.False(vm.LastRunSucceeded);
        Assert.Contains("ServiceClient failed to connect", vm.LastFailureMessage, StringComparison.Ordinal);
        Assert.Contains("Nothing was rolled back", vm.OutcomeDetail, StringComparison.Ordinal);
    }

    private sealed class MutableAuth : IAuthService
    {
        public ConnectionProfile? ActiveProfile { get; set; }
        public string? CurrentUserDisplayName { get; set; }
        public event EventHandler? SignedOut;
        public event EventHandler? ActiveProfileChanged;

        public Task<AuthResult> SignInAsync(nint parentHwnd, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult(false, null, "not used"));

        public Task<AuthResult> SignInAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult(true, CurrentUserDisplayName, null));

        public Task<AuthResult> TryConnectAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult(false, null, null));

        public Task SignOutAsync(CancellationToken ct = default)
        {
            ActiveProfile = null;
            CurrentUserDisplayName = null;
            SignedOut?.Invoke(this, EventArgs.Empty);
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task ForgetProfileAsync(ConnectionProfile profile, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default) => Task.FromResult("token");

        public void RaiseChanged() => ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }
}

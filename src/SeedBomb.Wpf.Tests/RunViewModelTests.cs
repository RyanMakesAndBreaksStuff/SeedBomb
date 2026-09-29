using SeedBomb.Core.Contracts;
using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Generation;
using SeedBomb.Services.History;
using SeedBomb.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class RunViewModelTests
{
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
            () => vm.ExecuteAsync(config, "contoso-dev", ["account"], 3, TestContext.Current.CancellationToken));

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
            "contoso-dev", ["account"], 1, TestContext.Current.CancellationToken);

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
            environmentHost: "", ["account"], 1, TestContext.Current.CancellationToken);

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
}

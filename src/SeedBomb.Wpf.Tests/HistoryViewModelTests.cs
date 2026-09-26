using Microsoft.Extensions.Logging;
using Moq;
using SeedBomb.Services.History;
using SeedBomb.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class HistoryViewModelTests
{
    [Fact]
    public async Task GroupsRunsByDayDescending()
    {
        var history = new Mock<IRunHistoryService>();
        history.Setup(h => h.GetRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new RunRecord(Guid.NewGuid(), new DateTimeOffset(2026, 8, 12, 14, 0, 0, TimeSpan.Zero),
                ["Account"], 10, TimeSpan.FromMinutes(1), true, 0),
            new RunRecord(Guid.NewGuid(), new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
                ["Contact"], 5, TimeSpan.FromMinutes(1), false, 2),
            new RunRecord(Guid.NewGuid(), new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero),
                ["Lead"], 1, TimeSpan.FromMinutes(1), true, 0),
        ]);

        var vm = new HistoryViewModel(history.Object, Mock.Of<ILogger<HistoryViewModel>>());
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.DayGroups.Count);
        Assert.Equal(2, vm.DayGroups[0].Runs.Count);
        Assert.Single(vm.DayGroups[1].Runs);
    }

    [Fact]
    public async Task SearchText_MatchesEnvironment()
    {
        var history = new Mock<IRunHistoryService>();
        history.Setup(h => h.GetRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new RunRecord(Guid.NewGuid(), new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero),
                ["Account"], 10, TimeSpan.FromMinutes(1), true, 0,
                Environment: "contoso.crm.dynamics.com"),
            new RunRecord(Guid.NewGuid(), new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero),
                ["Contact"], 5, TimeSpan.FromMinutes(1), true, 0,
                Environment: "fabrikam.crm.dynamics.com"),
        ]);

        var vm = new HistoryViewModel(history.Object, Mock.Of<ILogger<HistoryViewModel>>());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SearchText = "contoso";

        Assert.Single(vm.DayGroups);
        Assert.Single(vm.DayGroups[0].Runs);
        Assert.Equal("contoso.crm.dynamics.com", vm.DayGroups[0].Runs[0].Environment);
    }

    [Fact]
    public async Task LoadAsync_ShowsDangerSnackbar_WhenHistoryServiceFails()
    {
        var history = new Mock<IRunHistoryService>();
        history.Setup(h => h.GetRunsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("history corrupt"));

        var snackbar = new Mock<ISnackbarService>();
        var vm = new HistoryViewModel(
            history.Object, Mock.Of<ILogger<HistoryViewModel>>(), snackbar: snackbar.Object);

        await vm.LoadCommand.ExecuteAsync(null);

        snackbar.Verify(s => s.Show(
            "Couldn't load run history", "history corrupt",
            ControlAppearance.Danger, null, It.IsAny<TimeSpan>()),
            Times.Once);
    }

    [Fact]
    public async Task ClearHistoryAsync_ShowsDangerSnackbar_WhenClearFails()
    {
        var history = new Mock<IRunHistoryService>();
        history.Setup(h => h.ClearAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("locked"));

        var snackbar = new Mock<ISnackbarService>();
        var vm = new HistoryViewModel(
            history.Object, Mock.Of<ILogger<HistoryViewModel>>(), snackbar: snackbar.Object);

        await vm.ClearHistoryCommand.ExecuteAsync(null);

        snackbar.Verify(s => s.Show(
            "Couldn't clear history", "locked",
            ControlAppearance.Danger, null, It.IsAny<TimeSpan>()),
            Times.Once);
    }

    [Fact]
    public async Task ExportCsvAsync_ShowsSuccessSnackbar_WhenWriteSucceeds()
    {
        var history = new Mock<IRunHistoryService>();
        history.Setup(h => h.GetRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var snackbar = new Mock<ISnackbarService>();
        var vm = new HistoryViewModel(
            history.Object, Mock.Of<ILogger<HistoryViewModel>>(), snackbar: snackbar.Object);
        await vm.LoadCommand.ExecuteAsync(null);

        var exportDir = Directory.CreateTempSubdirectory("seedbomb-history-export-test-");
        vm.ExportDirectoryOverride = exportDir.FullName;
        try
        {
            string? exportedPath = null;
            snackbar
                .Setup(s => s.Show(
                    "History exported", It.IsAny<string>(),
                    ControlAppearance.Success, null, It.IsAny<TimeSpan>()))
                .Callback<string, string, ControlAppearance, IconElement?, TimeSpan>(
                    (_, message, _, _, _) => exportedPath = message);

            await vm.ExportCsvCommand.ExecuteAsync(null);

            snackbar.Verify(s => s.Show(
                "History exported", It.IsAny<string>(),
                ControlAppearance.Success, null, It.IsAny<TimeSpan>()),
                Times.Once);
            Assert.False(string.IsNullOrWhiteSpace(exportedPath));
            Assert.Equal(exportDir.FullName, Path.GetDirectoryName(exportedPath));
            Assert.True(File.Exists(exportedPath));
        }
        finally
        {
            exportDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void OpenRun_HistoricalThenLive_LiveSummaryIsIntact()
    {
        var live = new RunViewModel(generation: Mock.Of<SeedBomb.Services.Generation.IWpfGenerationService>());
        live.ApplyResult(new SeedBomb.Core.Contracts.GenerationResult
        {
            Elapsed = TimeSpan.FromMinutes(1),
            Errors = [new SeedBomb.Core.Contracts.BatchError("account", 0, "request throttled", -2147015902, 7)],
        }, seed: 42, environmentHost: "contoso-dev",
            new SeedBomb.Core.Contracts.GenerationConfig {
                EntityLogicalNames = ["account"],
                RecordCounts = new Dictionary<string, int> { ["account"] = 7 },
                Seed = 42,
            });
        var liveLog = live.ActivityLines;
        var vm = new HistoryViewModel(
            Mock.Of<IRunHistoryService>(), Mock.Of<ILogger<HistoryViewModel>>(), live);

        vm.OpenRunCommand.Execute(new RunRecord(Guid.NewGuid(), DateTimeOffset.Now, ["contact"], 3,
            TimeSpan.FromSeconds(2), true, 0, ActivityLog: ["old run"]));
        Assert.NotSame(live, live.SummaryView);
        Assert.Equal(["old run"], live.SummaryView.ActivityLines);

        vm.OpenRunCommand.Execute(new RunRecord(live.CurrentRunId, DateTimeOffset.Now, ["account"], 0,
            TimeSpan.FromMinutes(1), false, 7));
        Assert.Same(live, live.SummaryView);
        Assert.Equal(7, Assert.Single(live.RejectionGroups).RowCount);
        Assert.True(live.RetrySelectedCommand.CanExecute(null));
        Assert.Equal(liveLog, live.ActivityLines);
        Assert.Contains("seed 42", live.RunMetaLine, StringComparison.Ordinal);
    }
}

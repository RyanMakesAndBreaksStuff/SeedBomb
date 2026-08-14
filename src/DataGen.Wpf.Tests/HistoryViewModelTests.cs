using Seedbomb.Services.History;
using Seedbomb.ViewModels;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DataGen.Wpf.Tests;

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
}

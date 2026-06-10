using DataGen.Core.Contracts;
using DataGen.Desktop.Services.Generation;
using DataGen.Desktop.Services.History;
using DataGen.Desktop.ViewModels;
using Microsoft.Extensions.Logging;
using Moq;
using Wpf.Ui;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class GenerateViewModelStepTests
{
    [Fact]
    public void ExecuteStepTurnsCompleteAfterResultIsAvailable()
    {
        var viewModel = CreateViewModel();

        viewModel.OnEntitiesChanged(
        [
            new EntitySummary("account", "Account", false),
        ]);
        viewModel.LastResult = new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [Guid.NewGuid()],
            },
            Elapsed = TimeSpan.FromSeconds(1),
        };

        var executeStep = Assert.Single(viewModel.Steps, step => step.Label == "Execute");

        Assert.Equal("✓", executeStep.Glyph);
        Assert.True(executeStep.IsDone);
        Assert.False(executeStep.IsActive);
    }

    private static GenerateViewModel CreateViewModel() =>
        new(
            Mock.Of<IWpfGenerationService>(),
            Mock.Of<IRunHistoryService>(),
            Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>());
}

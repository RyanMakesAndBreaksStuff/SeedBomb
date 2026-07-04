using DataGen.Core.Contracts;
using Seedbomb.Services.Generation;
using Seedbomb.Services.History;
using Seedbomb.Services.Settings;
using Seedbomb.ViewModels;
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
            Mock.Of<ISettingsService>(),
            Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>());
}

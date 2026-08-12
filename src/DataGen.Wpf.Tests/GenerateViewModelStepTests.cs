using DataGen.Core.Contracts;
using DataGen.Core.Metadata;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.Services.Generation;
using Seedbomb.Services.History;
using Seedbomb.Services.Settings;
using Seedbomb.ViewModels;
using Seedbomb.ViewModels.Controls;
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
        var viewModel = CreateViewModel(out _, out _, out _);

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

    [Fact]
    public void StepsHasFiveEntriesWithExpectedLabels()
    {
        var viewModel = CreateViewModel(out _, out _, out _);

        Assert.Equal(5, viewModel.Steps.Count);
        Assert.Equal(
            ["Select", "Configure", "Rules", "Review", "Execute"],
            viewModel.Steps.Select(s => s.Label).ToArray());
    }

    [Fact]
    public async Task AdvancingToRulesLoadsMetadataForSelectedEntities()
    {
        var meta = BuildAccountMetadata();
        var viewModel = CreateViewModel(out _, out var metadataMock, out _);
        metadataMock
            .Setup(m => m.GetEntitiesAsync(
                It.Is<string[]>(names => names.SequenceEqual(new[] { "account" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntityMetadata>)[meta]);

        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        await viewModel.GoToRulesCommand.ExecutionTask!;

        metadataMock.Verify(
            m => m.GetEntitiesAsync(
                It.Is<string[]>(names => names.SequenceEqual(new[] { "account" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.True(viewModel.IsRulesLoaded);
        Assert.True(viewModel.EntityMetadataMap.ContainsKey("account"));
        Assert.Same(meta, viewModel.EntityMetadataMap["account"]);
    }

    [Fact]
    public async Task PreflightErrorsBlockStart()
    {
        var viewModel = await CreateReadyForRulesAsync(out var fieldRules, out _, out _, out _);

        // "name" is a string column — a numeric constant is an Error per RuleValidator.ValidateConstant.
        fieldRules.SetRule("account", "name",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("123").RootElement), "Name", "123");

        viewModel.GoToReviewCommand.Execute(null);

        Assert.True(viewModel.ReviewHasErrors);
        Assert.False(viewModel.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task WarningOnlyClampReturnsEffectiveRuleInReviewedMap()
    {
        var viewModel = await CreateReadyForRulesAsync(out var fieldRules, out _, out _, out _);

        var authored = new RangeRule(
            System.Text.Json.JsonDocument.Parse("-500").RootElement,
            System.Text.Json.JsonDocument.Parse("5000").RootElement);
        fieldRules.SetRule("account", "numberofemployees", authored, "Number of Employees", "0");

        viewModel.GoToReviewCommand.Execute(null);

        Assert.False(viewModel.ReviewHasErrors);
        Assert.NotNull(viewModel.ReviewedRules);
        var effective = Assert.IsType<RangeRule>(viewModel.ReviewedRules!["account"]["numberofemployees"]);

        Assert.NotSame(authored, effective);
        Assert.Equal(0m, effective.Min.GetDecimal());
        Assert.Equal(1000m, effective.Max.GetDecimal());
    }

    [Fact]
    public async Task EditingRuleAfterPreflightInvalidatesReviewedSnapshotAndBlocksStart()
    {
        var viewModel = await CreateReadyForRulesAsync(out var fieldRules, out _, out _, out _);
        fieldRules.SetRule("account", "name",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("\"Acme\"").RootElement), "Name", "Acme");

        viewModel.GoToReviewCommand.Execute(null);
        Assert.NotNull(viewModel.ReviewedRules);

        // Any further draft mutation — edit or remove — must invalidate the snapshot.
        fieldRules.RemoveRule("account", "name");

        Assert.Null(viewModel.ReviewedRules);
        Assert.False(viewModel.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task GenerateAsyncPassesReviewedMapAndRunIdVerbatim()
    {
        var viewModel = await CreateReadyForRulesAsync(out var fieldRules, out var generationMock, out _, out _);
        fieldRules.SetRule("account", "name",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("\"Acme\"").RootElement), "Name", "Acme");
        viewModel.GoToReviewCommand.Execute(null);
        Assert.True(viewModel.GenerateCommand.CanExecute(null));

        var reviewedSnapshot = viewModel.ReviewedRules;
        var reviewRunId = viewModel.RunId;

        GenerationConfig? captured = null;
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Callback<GenerationConfig, IProgress<ProgressUpdate>, CancellationToken>((cfg, _, _) => captured = cfg)
            .ReturnsAsync(new GenerationResult());

        await viewModel.GenerateCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Same(reviewedSnapshot, captured!.FieldRules);
        Assert.Equal(reviewRunId, captured.RunId);
    }

    [Fact]
    public async Task CancelDiscardsDraftAndNeverCallsGenerate()
    {
        var viewModel = await CreateReadyForRulesAsync(out var fieldRules, out var generationMock, out _, out _);

        // Nothing has been committed yet — draft additions must vanish on Cancel.
        fieldRules.SetRule("account", "name",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("\"Acme\"").RootElement), "Name", "Acme");
        Assert.NotEmpty(fieldRules.Rows);

        viewModel.CancelDraftCommand.Execute(null);

        Assert.Empty(fieldRules.Rows);
        Assert.Null(viewModel.ReviewedRules);
        Assert.False(viewModel.IsReviewOpen);
        generationMock.Verify(
            g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static GenerateViewModel CreateViewModel(
        out Mock<IWpfGenerationService> generationMock,
        out Mock<IMetadataProvider> metadataMock,
        out Mock<IRunHistoryService> historyMock)
    {
        generationMock = new Mock<IWpfGenerationService>();
        metadataMock = new Mock<IMetadataProvider>();
        historyMock = new Mock<IRunHistoryService>();

        return new GenerateViewModel(
            generationMock.Object,
            historyMock.Object,
            Mock.Of<ISettingsService>(),
            Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(),
            metadataMock.Object);
    }

    /// <summary>Builds a view-model with metadata already loaded and the board attached, ready for SetRule + Review.</summary>
    private static Task<GenerateViewModel> CreateReadyForRulesAsync(
        out FieldRulesViewModel fieldRules,
        out Mock<IWpfGenerationService> generationMock,
        out Mock<IMetadataProvider> metadataMock,
        out Mock<IRunHistoryService> historyMock)
    {
        var meta = BuildAccountMetadata();
        var viewModel = CreateViewModel(out generationMock, out metadataMock, out historyMock);
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntityMetadata>)[meta]);

        fieldRules = new FieldRulesViewModel();
        viewModel.AttachFieldRules(fieldRules);

        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        return AwaitRulesLoaded();

        // async methods can't have out params -- the await moves into this local function instead.
        async Task<GenerateViewModel> AwaitRulesLoaded()
        {
            await viewModel.GoToRulesCommand.ExecutionTask!;
            Assert.True(viewModel.IsRulesLoaded);
            return viewModel;
        }
    }

    // EntityMetadata.Attributes setter is non-public — same reflection-set pattern used by
    // RuleEditorViewModelTests / DataGen.Bulk.Tests/RuledGenerationTests.
    private static EntityMetadata BuildAccountMetadata()
    {
        var name = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 100 };
        var employees = new IntegerAttributeMetadata { LogicalName = "numberofemployees", IsValidForCreate = true, MinValue = 0, MaxValue = 1000 };

        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[] { name, employees });
        return meta;
    }
}

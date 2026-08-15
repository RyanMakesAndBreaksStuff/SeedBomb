using System.Windows.Media;
using DataGen.Core.Contracts;
using DataGen.Core.Metadata;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.Services.Generation;
using Seedbomb.Services.History;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Profiles;
using Seedbomb.Services.Settings;
using Seedbomb.ViewModels;
using Seedbomb.ViewModels.Controls;
using Seedbomb.Views.Pages;
using Microsoft.Extensions.Logging;
using Moq;
using Wpf.Ui;
using Wpf.Ui.Controls;
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

        var executeStep = Assert.Single(viewModel.Steps, step => step.Label == "Run");

        Assert.Equal("✓", executeStep.Glyph);
        Assert.True(executeStep.IsDone);
        Assert.False(executeStep.IsActive);
    }

    [Fact]
    public void StepsHasFourEntriesWithExpectedLabels()
    {
        var viewModel = CreateViewModel(out _, out _, out _);

        Assert.Equal(4, viewModel.Steps.Count);
        Assert.Equal(
            ["Tables", "Volume & rules", "Review", "Run"],
            viewModel.Steps.Select(s => s.Label).ToArray());
    }

    [Fact]
    public void GoNextOnEmptyTablesDoesNotAdvance()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        Assert.Equal(0, viewModel.CurrentStep);
        Assert.False(viewModel.GoNextCommand.CanExecute(null));
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        Assert.True(viewModel.GoNextCommand.CanExecute(null));
    }

    [Fact]
    public async Task GoNextFromTablesLoadsMetadataAndAdvances()
    {
        var viewModel = await CreateReadyForRulesAsync(out _, out _, out _, out _);
        Assert.True(viewModel.IsRulesLoaded);
        Assert.Equal(1, viewModel.CurrentStep);
    }

    [Fact]
    public async Task GoToReviewCommandBecomesExecutableAfterRulesLoad()
    {
        var meta = BuildAccountMetadata();
        var viewModel = CreateViewModel(out _, out var metadataMock, out _);
        var tcs = new TaskCompletionSource<IReadOnlyList<EntityMetadata>>();
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        var loadTask = viewModel.GoToRulesCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRulesLoaded);
        Assert.False(viewModel.GoToReviewCommand.CanExecute(null));

        tcs.SetResult([meta]);
        await loadTask;

        Assert.True(viewModel.IsRulesLoaded);
        Assert.True(viewModel.GoToReviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task GoToRulesFailureShowsDangerSnackbar()
    {
        var viewModel = CreateViewModel(out _, out var metadataMock, out _, out var snackbarMock);
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("org unreachable"));

        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        await viewModel.GoToRulesCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRulesLoaded);
        snackbarMock.Verify(
            s => s.Show(
                "Rules metadata failed",
                "org unreachable",
                ControlAppearance.Danger,
                null,
                It.IsAny<TimeSpan>()),
            Times.Once);
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
        await viewModel.GoToRulesCommand.ExecuteAsync(null);

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
    public async Task OnNavigatedToAppliesSettingsAndNotifiesDefaultRecordCount()
    {
        var settingsMock = new Mock<ISettingsService>();
        settingsMock
            .Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings(
                OrgUrl: string.Empty,
                ClientId: string.Empty,
                TenantId: string.Empty,
                DefaultRecordCount: 25,
                DefaultBatchSize: 250,
                DefaultDop: 4));

        var viewModel = new GenerateViewModel(
            Mock.Of<IWpfGenerationService>(),
            Mock.Of<IRunHistoryService>(),
            settingsMock.Object,
            Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(),
            Mock.Of<IMetadataProvider>(),
            Mock.Of<IProfileService>(),
            Mock.Of<IContentDialogService>(),
            new RunViewModel(Mock.Of<IWpfGenerationService>()));

        var notified = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                notified.Add(e.PropertyName);
        };

        await viewModel.OnNavigatedToAsync();

        Assert.Equal(25, viewModel.DefaultRecordCount);
        Assert.Equal(250, viewModel.BatchSize);
        Assert.Equal(4, viewModel.MaxParallelism);
        Assert.Contains(nameof(GenerateViewModel.DefaultRecordCount), notified);
    }

    [Fact]
    public void QueueDotBrushFollowsProgressAndErrors()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.OnEntitiesChanged(
        [
            new EntitySummary("account", "Account", false),
            new EntitySummary("contact", "Contact", false),
        ]);

        Assert.All(viewModel.QueuedEntities, e => Assert.Equal(Brushes.LightGray, e.DotBrush));

        viewModel.CurrentProgress = new ProgressUpdate(
            "Generating", "account", 1, 10, 1, 2, 0, TimeSpan.Zero);

        var accountDot = Assert.Single(viewModel.QueuedEntities, e => e.Entity.LogicalName == "account");
        Assert.NotEqual(Brushes.LightGray, accountDot.DotBrush);

        viewModel.LastResult = new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [Guid.NewGuid()],
            },
            Errors = [new BatchError("contact", 0, "failed", null)],
        };

        Assert.NotEqual(Brushes.LightGray, viewModel.QueuedEntities.Single(e => e.Entity.LogicalName == "account").DotBrush);
        Assert.NotEqual(Brushes.LightGray, viewModel.QueuedEntities.Single(e => e.Entity.LogicalName == "contact").DotBrush);
        Assert.NotEqual(
            viewModel.QueuedEntities.Single(e => e.Entity.LogicalName == "account").DotBrush,
            viewModel.QueuedEntities.Single(e => e.Entity.LogicalName == "contact").DotBrush);
    }

    [Fact]
    public void LastRunStatusTextReportsErrorsWhenPresent()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.LastResult = new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [Guid.NewGuid()],
            },
            Errors =
            [
                new BatchError("account", 0, "plugin failed", 123),
            ],
        };

        Assert.True(viewModel.LastRunHasErrors);
        Assert.Contains("plugin failed", viewModel.LastRunStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateSnackbarUsesCautionWhenResultHasErrors()
    {
        var viewModel = await CreateReadyForRulesAsync(out _, out var generationMock, out _, out _, out var snackbarMock);
        viewModel.GoToReviewCommand.Execute(null);
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
                Errors = [new BatchError("account", 0, "plugin failed", null)],
            });

        await viewModel.GenerateCommand.ExecuteAsync(null);

        snackbarMock.Verify(
            s => s.Show(
                "Completed with errors",
                It.Is<string>(m => m.Contains("1", StringComparison.Ordinal)),
                ControlAppearance.Caution,
                null,
                It.IsAny<TimeSpan>()),
            Times.Once);
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
        Assert.Equal(0, viewModel.CurrentStep);
        generationMock.Verify(
            g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnNavigatedTo_does_not_overwrite_batch_when_tables_already_selected()
    {
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings("", "", "", 10, 250, 4));
        var viewModel = new GenerateViewModel(
            Mock.Of<IWpfGenerationService>(), Mock.Of<IRunHistoryService>(),
            settingsMock.Object, Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(), Mock.Of<IMetadataProvider>(),
            Mock.Of<IProfileService>(), Mock.Of<IContentDialogService>(),
            new RunViewModel(Mock.Of<IWpfGenerationService>()));

        await viewModel.OnNavigatedToAsync();
        viewModel.BatchSize = 900;
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        viewModel.CurrentStep = 1;

        await viewModel.OnNavigatedToAsync();

        Assert.Equal(900, viewModel.BatchSize);
        Assert.Equal(1, viewModel.CurrentStep);
        Assert.Single(viewModel.SelectedEntities);
    }

    [Fact]
    public void OnEntitiesChanged_same_set_does_not_reset_step()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        var account = new EntitySummary("account", "Account", false);
        viewModel.OnEntitiesChanged([account]);
        viewModel.CurrentStep = 2;
        viewModel.IsRulesLoaded = true;

        viewModel.OnEntitiesChanged([account]);

        Assert.Equal(2, viewModel.CurrentStep);
        Assert.True(viewModel.IsRulesLoaded);
    }

    [Fact]
    public async Task OnNavigatedFrom_writes_working_set_request()
    {
        var request = new RulesNavigationRequest();
        var viewModel = new GenerateViewModel(
            Mock.Of<IWpfGenerationService>(), Mock.Of<IRunHistoryService>(),
            Mock.Of<ISettingsService>(), Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(), Mock.Of<IMetadataProvider>(),
            Mock.Of<IProfileService>(), Mock.Of<IContentDialogService>(),
            new RunViewModel(Mock.Of<IWpfGenerationService>()),
            request,
            Mock.Of<IAppNavigator>());
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);

        await viewModel.OnNavigatedFromAsync();

        Assert.NotNull(request.Profile);
        Assert.Equal(typeof(GeneratePage), request.ReturnPage);
        Assert.NotNull(request.OnSaved);
    }

    [Fact]
    public async Task Reset_clears_tables_rules_seed_and_draft()
    {
        var profiles = new Mock<IProfileService>();
        var viewModel = new GenerateViewModel(
            Mock.Of<IWpfGenerationService>(), Mock.Of<IRunHistoryService>(),
            Mock.Of<ISettingsService>(), Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(), Mock.Of<IMetadataProvider>(),
            profiles.Object, Mock.Of<IContentDialogService>(),
            new RunViewModel(Mock.Of<IWpfGenerationService>()))
        {
            ConfirmReset = () => Task.FromResult(true),
        };

        var fieldRules = new FieldRulesViewModel();
        viewModel.AttachFieldRules(fieldRules);
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        fieldRules.SelectTable("account");
        fieldRules.SetRule("account", "name",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("\"Acme\"").RootElement), "Name", "Acme");
        Assert.NotEmpty(fieldRules.Rows);
        viewModel.Seed = 99;
        viewModel.CurrentStep = 1;
        viewModel.ActiveProfileName = "acme-sales-scenario";

        await viewModel.ResetCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.SelectedEntities);
        Assert.Equal(0, viewModel.CurrentStep);
        Assert.Empty(fieldRules.Rows);
        Assert.Equal(42, viewModel.Seed);
        Assert.Equal("No profile loaded", viewModel.ActiveProfileName);
        profiles.Verify(p => p.ClearDraftAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SelectedTableRows_uses_default_count_per_table()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.DefaultRecordCount = 50;
        viewModel.OnEntitiesChanged(
        [
            new EntitySummary("account", "Account", false),
            new EntitySummary("contact", "Contact", false),
        ]);

        Assert.Equal(2, viewModel.SelectedTableRows.Count);
        Assert.Equal("Account", viewModel.SelectedTableRows[0].DisplayName);
        Assert.Equal(50, viewModel.SelectedTableRows[0].Count);
        Assert.Equal(100, viewModel.PlannedTotal);
        Assert.Equal("No profile loaded", viewModel.ActiveProfileName);
    }

    [Fact]
    public void ProfileSummaryLine_includes_rule_count_and_seed()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.Seed = 40719;
        Assert.Contains("seed 40719", viewModel.ProfileSummaryLine, StringComparison.Ordinal);
        Assert.EndsWith("· en", viewModel.ProfileSummaryLine);
    }

    [Fact]
    public void Locale_is_english_and_read_only()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        Assert.Equal("en", viewModel.Locale);
        Assert.Contains(viewModel.RunPlanStats, row => row.Label == "Locale" && row.Value == "en");
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static GenerateViewModel CreateViewModel(
        out Mock<IWpfGenerationService> generationMock,
        out Mock<IMetadataProvider> metadataMock,
        out Mock<IRunHistoryService> historyMock,
        out Mock<ISnackbarService> snackbarMock)
    {
        generationMock = new Mock<IWpfGenerationService>();
        metadataMock = new Mock<IMetadataProvider>();
        historyMock = new Mock<IRunHistoryService>();
        snackbarMock = new Mock<ISnackbarService>();

        return new GenerateViewModel(
            generationMock.Object,
            historyMock.Object,
            Mock.Of<ISettingsService>(),
            snackbarMock.Object,
            Mock.Of<ILogger<GenerateViewModel>>(),
            metadataMock.Object,
            Mock.Of<IProfileService>(),
            Mock.Of<IContentDialogService>(),
            new RunViewModel(generationMock.Object));
    }

    private static GenerateViewModel CreateViewModel(
        out Mock<IWpfGenerationService> generationMock,
        out Mock<IMetadataProvider> metadataMock,
        out Mock<IRunHistoryService> historyMock)
        => CreateViewModel(out generationMock, out metadataMock, out historyMock, out _);

    /// <summary>Builds a view-model with metadata already loaded and the board attached, ready for SetRule + Review.</summary>
    private static Task<GenerateViewModel> CreateReadyForRulesAsync(
        out FieldRulesViewModel fieldRules,
        out Mock<IWpfGenerationService> generationMock,
        out Mock<IMetadataProvider> metadataMock,
        out Mock<IRunHistoryService> historyMock)
        => CreateReadyForRulesAsync(out fieldRules, out generationMock, out metadataMock, out historyMock, out _);

    private static Task<GenerateViewModel> CreateReadyForRulesAsync(
        out FieldRulesViewModel fieldRules,
        out Mock<IWpfGenerationService> generationMock,
        out Mock<IMetadataProvider> metadataMock,
        out Mock<IRunHistoryService> historyMock,
        out Mock<ISnackbarService> snackbarMock)
    {
        var meta = BuildAccountMetadata();
        var viewModel = CreateViewModel(out generationMock, out metadataMock, out historyMock, out snackbarMock);
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
            await viewModel.GoNextCommand.ExecuteAsync(null);
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

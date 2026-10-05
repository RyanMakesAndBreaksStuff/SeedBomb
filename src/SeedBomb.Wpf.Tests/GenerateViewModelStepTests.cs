using SeedBomb.Core.Contracts;
using SeedBomb.Core.Metadata;
using SeedBomb.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Generation;
using SeedBomb.Services.History;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels;
using SeedBomb.ViewModels.Controls;
using SeedBomb.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

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
    public void ReviewHasErrors_FollowsReviewMessages()
    {
        var viewModel = CreateViewModel(out _, out _, out _);

        viewModel.Review = new ReviewSnapshot(new(), [new RuleMessage(RuleMessageSeverity.Error, "boom")], 0, []);
        Assert.True(viewModel.ReviewHasErrors);

        viewModel.Review = new ReviewSnapshot(new(), [new RuleMessage(RuleMessageSeverity.Warning, "heads up")], 0, []);
        Assert.False(viewModel.ReviewHasErrors);
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
    public async Task GenerateAsync_ClampsMaxParallelismToMaxDop()
    {
        var viewModel = await CreateReadyForRulesAsync(out var fieldRules, out var generationMock, out _, out _);
        fieldRules.SetRule("account", "name",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("\"Acme\"").RootElement), "Name", "Acme");
        viewModel.GoToReviewCommand.Execute(null);
        viewModel.MaxParallelism = 64;

        GenerationConfig? captured = null;
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Callback<GenerationConfig, IProgress<ProgressUpdate>, CancellationToken>((cfg, _, _) => captured = cfg)
            .ReturnsAsync(new GenerationResult());

        await viewModel.GenerateCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(GenerationLimits.MaxDop, captured!.MaxParallelism);
    }

    [Fact]
    public async Task GoToReview_BogusRule_UsesContextAndSessionPreview()
    {
        var viewModel = await CreateReadyForRulesAsync(out var fieldRules, out _, out _, out _);
        fieldRules.SetRule("account", "name", new BogusRule("NAME", "firstName", 1), "Name", "NAME.firstName");

        viewModel.GoToReviewCommand.Execute(null);

        Assert.False(viewModel.ReviewHasErrors);
        var effective = Assert.IsType<BogusRule>(viewModel.ReviewedRules!["account"]["name"]);
        Assert.Equal("NAME", effective.Api);
        Assert.Equal(5, viewModel.ReviewPreviewRows.Single().Values.Count);
        Assert.Equal("Kurtis", viewModel.ReviewPreviewRows.Single().Values[0]);
    }

    [Fact]
    public async Task GoToReview_BogusRule_ExceedingMaxLength_SurfacesAsReviewErrorInsteadOfThrowing()
    {
        // MaxLength 3 guarantees an overflow: NAME.firstName/row 0/seed 42 deterministically
        // generates "Kurtis" (6 chars) — see GoToReview_BogusRule_UsesContextAndSessionPreview.
        var name = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 3 };
        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[] { name });

        var viewModel = CreateViewModel(out _, out var metadataMock, out _);
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntityMetadata>)[meta]);
        var fieldRules = new FieldRulesViewModel();
        viewModel.AttachFieldRules(fieldRules);
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        await viewModel.GoNextCommand.ExecuteAsync(null);

        fieldRules.SetRule("account", "name", new BogusRule("NAME", "firstName", 1), "Name", "NAME.firstName");

        viewModel.GoToReviewCommand.Execute(null);

        Assert.True(viewModel.ReviewHasErrors);
        Assert.Contains(viewModel.ReviewMessages,
            m => m.Severity == RuleMessageSeverity.Error && m.Text.Contains("MaxLength", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnNavigatedToAppliesSettingsAndNotifiesDefaultRecordCount()
    {
        var settingsMock = new Mock<ISettingsService>();
        settingsMock
            .Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings(
                DefaultRecordCount: 25,
                DefaultBatchSize: 250,
                DefaultDop: 4));

        var viewModel = new GenerateViewModel(
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
    public async Task GenerateSnackbarUsesCautionWhenResultHasErrors()
    {
        var viewModel = await CreateReadyForRulesAsync(out _, out var generationMock, out _, out _, out var snackbarMock);
        viewModel.GoToReviewCommand.Execute(null);
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
                Errors = [new BatchError("account", "plugin failed", null)],
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
    public async Task GenerateAsync_DoesNotFault_WhenDraftPersistThrows()
    {
        var profileService = new Mock<IProfileService>();
        profileService.Setup(p => p.SaveDraftAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("draft store unavailable"));

        var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new Mock<ILogger<GenerateViewModel>>();
        logger
            .Setup(x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() => logged.TrySetResult());

        var viewModel = CreateViewModel(
            out var generationMock, out _, out _, out _,
            profileService: profileService.Object,
            logger: logger.Object);
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult());
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);

        var ex = await Record.ExceptionAsync(() => viewModel.GenerateCommand.ExecuteAsync(null));

        Assert.Null(ex);
        // F&F PersistDraftAsync must catch + LogWarning; without WR-006 this times out (task faults unobserved).
        await logged.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        profileService.Verify(
            p => p.SaveDraftAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DraftClearFailure_IsShownOncePerSession()
    {
        // WR-009: clear failures were Debug-logged (dropped by the file logger), so a board the user
        // reset silently came back next launch.
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ClearDraftAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("draft.json is locked"));
        var viewModel = CreateViewModel(out _, out _, out _, out var snackbarMock, profileService: profiles.Object);
        viewModel.ConfirmReset = () => Task.FromResult(true);

        await viewModel.ResetCommand.ExecuteAsync(null);
        await viewModel.ResetCommand.ExecuteAsync(null);

        snackbarMock.Verify(s => s.Show(
            "Generate draft", It.Is<string>(m => m.Contains("draft.json is locked")),
            ControlAppearance.Caution, null, It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task OnNavigatedTo_does_not_overwrite_batch_when_tables_already_selected()
    {
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings(10, 250, 4));
        var viewModel = new GenerateViewModel(
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
    public async Task Leaving_Generate_leaves_the_rules_request_alone()
    {
        // CR-004: leaving Generate used to stamp its callback and return page on the shared request,
        // and Edit rules on the Profiles page then inherited them.
        var request = new RulesNavigationRequest();
        var viewModel = new GenerateViewModel(
            Mock.Of<ISettingsService>(), Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(), Mock.Of<IMetadataProvider>(),
            Mock.Of<IProfileService>(), Mock.Of<IContentDialogService>(),
            new RunViewModel(Mock.Of<IWpfGenerationService>()),
            request,
            Mock.Of<IAppNavigator>());
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);

        await viewModel.OnNavigatedFromAsync();

        Assert.Null(request.Profile);
        Assert.Null(request.OnSaved);
        Assert.Null(request.ReturnPage);
    }

    [Fact]
    public void EditRules_after_deselecting_a_ruled_table_leaves_it_off_the_rules_page()
    {
        var request = new RulesNavigationRequest();
        var viewModel = CreateViewModelWithRulesRequest(request);
        var account = new EntitySummary("account", "Account", false);
        viewModel.OnEntitiesChanged([account, new EntitySummary("contact", "Contact", false)]);
        viewModel.FieldRules.SetRule("contact", "firstname",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("\"Ada\"").RootElement), "First Name", "Ada");

        viewModel.OnEntitiesChanged([account]);
        viewModel.EditRulesCommand.Execute(null);

        Assert.Equal(["account"], request.Profile!.Tables.Select(t => t.Table));
        Assert.Equal(0, viewModel.DraftRuleCount);
    }

    [Fact]
    public void Rules_page_save_without_a_table_deselects_it_on_the_board()
    {
        var request = new RulesNavigationRequest();
        var viewModel = CreateViewModelWithRulesRequest(request);
        viewModel.OnEntitiesChanged(
        [
            new EntitySummary("account", "Account", false),
            new EntitySummary("contact", "Contact", false),
        ]);
        viewModel.EditRulesCommand.Execute(null);

        request.OnSaved!(new Profile(2, "working-set", null, 42, [new ProfileTable("account", 10, null)]));

        Assert.Equal(["account"], viewModel.SelectedEntities.Select(e => e.LogicalName));
    }

    [Fact]
    public void Profiles_rules_save_reaches_the_board_only_for_the_loaded_profile()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.OnEntitiesChanged([new EntitySummary("contact", "Contact", false)]);
        viewModel.ActiveProfileName = "contact-acct";
        viewModel.FieldRules.SetRule("contact", "address1_freighttermscode",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("1").RootElement), "Freight Terms", "1");
        var ruleDeleted = new Profile(2, "contact-acct", null, 42, [new ProfileTable("contact", 10, null)]);

        viewModel.ApplySavedProfileIfActive(ruleDeleted with { Name = "g2" });
        Assert.Equal(1, viewModel.DraftRuleCount);

        viewModel.ApplySavedProfileIfActive(ruleDeleted);
        Assert.Equal(0, viewModel.DraftRuleCount);
    }

    [Fact]
    public async Task Reset_clears_tables_rules_seed_and_draft()
    {
        var profiles = new Mock<IProfileService>();
        var viewModel = new GenerateViewModel(
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
        fieldRules.SetRule("account", "name",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("\"Acme\"").RootElement), "Name", "Acme");
        Assert.NotEmpty(fieldRules.GetRules());
        viewModel.Seed = 99;
        viewModel.CurrentStep = 1;
        viewModel.ActiveProfileName = "acme-sales-scenario";

        await viewModel.ResetCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.SelectedEntities);
        Assert.Equal(0, viewModel.CurrentStep);
        Assert.Empty(fieldRules.GetRules());
        Assert.Equal(42, viewModel.Seed);
        Assert.Equal("No profile loaded", viewModel.ActiveProfileName);
        profiles.Verify(p => p.ClearDraftAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void EditingACount_RaisesPlannedTotalAndRunConfirmationLine()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        var overrides = new FieldOverridesViewModel();
        viewModel.AttachFieldOverrides(overrides);

        var account = new EntitySummary("account", "Account", false);
        viewModel.OnEntitiesChanged([account]);
        overrides.SetEntities([account], defaultCount: 10);

        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        overrides.Entries.Single().Count = 1000;

        Assert.Contains(nameof(GenerateViewModel.PlannedTotal), raised);
        Assert.Contains(nameof(GenerateViewModel.RunConfirmationLine), raised);
        Assert.Equal(1000, viewModel.PlannedTotal);
        Assert.Contains(1000.ToString("N0"), viewModel.RunConfirmationLine);
    }

    [Fact]
    public void ChangingTableSelection_KeepsCountEditsObservable()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        var overrides = new FieldOverridesViewModel();
        viewModel.AttachFieldOverrides(overrides);

        var account = new EntitySummary("account", "Account", false);
        var contact = new EntitySummary("contact", "Contact", false);

        viewModel.OnEntitiesChanged([account]);
        overrides.SetEntities([account], defaultCount: 10);

        // SetEntities clears and rebuilds Entries — subscriptions taken at attach time are gone.
        viewModel.OnEntitiesChanged([account, contact]);
        overrides.SetEntities([account, contact], defaultCount: 10);

        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        overrides.Entries.Single(x => x.Entity.LogicalName == "contact").Count = 500;

        Assert.Contains(nameof(GenerateViewModel.PlannedTotal), raised);
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

    [Fact]
    public async Task CommitThenResetThenDiscard_LeavesNoPersistedDraft()
    {
        var root = Path.Combine(Path.GetTempPath(), "seedbomb-t3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var profiles = new JsonProfileService(root);
            var generationMock = new Mock<IWpfGenerationService>();
            var vm = new GenerateViewModel(
                Mock.Of<ISettingsService>(),
                Mock.Of<ISnackbarService>(),
                Mock.Of<ILogger<GenerateViewModel>>(),
                Mock.Of<IMetadataProvider>(),
                profiles,
                Mock.Of<IContentDialogService>(),
                new RunViewModel(generationMock.Object));

            var fieldRules = new FieldRulesViewModel();
            vm.AttachFieldRules(fieldRules);
            fieldRules.SetRule(
                "account",
                "name",
                new ConstantRule(System.Text.Json.JsonDocument.Parse("\"x\"").RootElement),
                "Name",
                "x");
            fieldRules.Commit();

            await Task.Delay(800, TestContext.Current.CancellationToken);
            var draftPath = Path.Combine(root, "draft.profile.json");
            Assert.True(File.Exists(draftPath));

            await vm.ResetWithoutPromptAsync();
            await Task.Delay(800, TestContext.Current.CancellationToken);

            Assert.Empty(fieldRules.GetRules());
            Assert.False(fieldRules.IsDirty);
            Assert.False(File.Exists(draftPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelled_run_that_wrote_rows_is_recorded_as_not_succeeded()
    {
        // WR-002: rows written before a cancel are not rolled back, so History must list them.
        var viewModel = CreateViewModel(out var generationMock, out _, out var historyMock);
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
                Cancelled = true,
            });
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);

        await viewModel.GenerateCommand.ExecuteAsync(null);

        historyMock.Verify(h => h.AddRunAsync(
            It.Is<RunRecord>(r => !r.Succeeded && r.TotalRecords == 1 && r.EntityNames.Single() == "Account"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Cancelled", viewModel.Run.StatusHeadline);
    }

    [Fact]
    public async Task Failed_run_is_recorded_as_not_succeeded()
    {
        var viewModel = CreateViewModel(out var generationMock, out _, out var historyMock);
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SeedBomb.Core.Exceptions.DataGenerationException("Entity 'account': required lookup 'parentid' (SystemRequired) has no generator — cannot create records."));
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);

        await viewModel.GenerateCommand.ExecuteAsync(null);

        historyMock.Verify(h => h.AddRunAsync(
            It.Is<RunRecord>(r => !r.Succeeded && r.TotalRecords == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task History_write_failure_is_not_reported_as_a_failed_run()
    {
        var viewModel = CreateViewModel(out var generationMock, out _, out var historyMock, out var snackbarMock);
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult());
        historyMock
            .Setup(h => h.AddRunAsync(It.IsAny<RunRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("history.json is locked"));
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);

        await viewModel.GenerateCommand.ExecuteAsync(null);

        snackbarMock.Verify(
            s => s.Show("Couldn't save run history", "history.json is locked", ControlAppearance.Danger, null, It.IsAny<TimeSpan>()),
            Times.Once);
        Assert.Equal("", viewModel.Run.LastFailureMessage);
    }

    [Fact]
    public void Step4_confirmation_names_the_environment_the_run_writes_to()
    {
        // CR-002: step 4 never said which org the rows would be written to.
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile)
            .Returns(new ConnectionProfile { EnvironmentUrl = "https://contoso-prod.crm.dynamics.com" });
        var viewModel = new GenerateViewModel(
            Mock.Of<ISettingsService>(), Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(), Mock.Of<IMetadataProvider>(), Mock.Of<IProfileService>(),
            Mock.Of<IContentDialogService>(), new RunViewModel(Mock.Of<IWpfGenerationService>(), auth: auth.Object));
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);

        Assert.Contains("to contoso-prod.crm.dynamics.com", viewModel.RunConfirmationLine, StringComparison.Ordinal);
    }

    private static GenerateViewModel CreateViewModel(
        out Mock<IWpfGenerationService> generationMock,
        out Mock<IMetadataProvider> metadataMock,
        out Mock<IRunHistoryService> historyMock,
        out Mock<ISnackbarService> snackbarMock,
        IProfileService? profileService = null,
        ILogger<GenerateViewModel>? logger = null)
    {
        generationMock = new Mock<IWpfGenerationService>();
        metadataMock = new Mock<IMetadataProvider>();
        historyMock = new Mock<IRunHistoryService>();
        snackbarMock = new Mock<ISnackbarService>();

        return new GenerateViewModel(
            Mock.Of<ISettingsService>(),
            snackbarMock.Object,
            logger ?? Mock.Of<ILogger<GenerateViewModel>>(),
            metadataMock.Object,
            profileService ?? Mock.Of<IProfileService>(),
            Mock.Of<IContentDialogService>(),
            new RunViewModel(generationMock.Object, snackbar: snackbarMock.Object, history: historyMock.Object));
    }

    private static GenerateViewModel CreateViewModel(
        out Mock<IWpfGenerationService> generationMock,
        out Mock<IMetadataProvider> metadataMock,
        out Mock<IRunHistoryService> historyMock)
        => CreateViewModel(out generationMock, out metadataMock, out historyMock, out _);

    /// <summary>Wires the Rules-page handoff so <c>EditRulesCommand</c> fills <paramref name="request"/>.</summary>
    private static GenerateViewModel CreateViewModelWithRulesRequest(RulesNavigationRequest request) =>
        new(
            Mock.Of<ISettingsService>(), Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(), Mock.Of<IMetadataProvider>(),
            Mock.Of<IProfileService>(), Mock.Of<IContentDialogService>(),
            new RunViewModel(Mock.Of<IWpfGenerationService>()),
            request,
            Mock.Of<IAppNavigator>());

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

    [Fact]
    public async Task GoToReview_LookupRandomRule_KeepsRuleAndExplanatoryPreview()
    {
        var lookup = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid",
            IsValidForCreate = true,
            Targets = ["account"],
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(lookup, AttributeTypeCode.Lookup);
        var meta = BuildAccountMetadata(lookup);
        var viewModel = CreateViewModel(out _, out var metadataMock, out _);
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntityMetadata>)[meta]);
        var fieldRules = new FieldRulesViewModel();
        viewModel.AttachFieldRules(fieldRules);
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        await viewModel.GoNextCommand.ExecuteAsync(null);

        fieldRules.SetRule("account", "parentaccountid", new LookupRandomRule(), "Parent Account", "preview");
        viewModel.GoToReviewCommand.Execute(null);

        Assert.False(viewModel.ReviewHasErrors);
        Assert.IsType<LookupRandomRule>(viewModel.ReviewedRules!["account"]["parentaccountid"]);
        var preview = Assert.Single(viewModel.ReviewPreviewRows.Single().Values);
        Assert.Contains("1,000", preview);
        Assert.Contains("Candidate validation happens at Start", preview);
        Assert.DoesNotContain("11111111", preview);
        // WR-005: Review and the Rules page must preview the same rule identically.
        Assert.Equal(new RuleEditorViewModel(meta, 10, 42, "r1").LookupRandomExplanation, preview);
    }

    [Fact]
    public async Task GoToReview_LookupConstant_FormatsEntityReference()
    {
        var lookup = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid",
            IsValidForCreate = true,
            Targets = ["account"],
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(lookup, AttributeTypeCode.Lookup);
        var meta = BuildAccountMetadata(lookup);
        var viewModel = CreateViewModel(out _, out var metadataMock, out _);
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntityMetadata>)[meta]);
        var fieldRules = new FieldRulesViewModel();
        viewModel.AttachFieldRules(fieldRules);
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        await viewModel.GoNextCommand.ExecuteAsync(null);

        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        fieldRules.SetRule("account", "parentaccountid",
            new ConstantRule(new LookupRuleValue("account", id, "Acme").ToJson()),
            "Parent Account", "preview");
        viewModel.GoToReviewCommand.Execute(null);

        Assert.False(viewModel.ReviewHasErrors);
        Assert.Equal($"account · {id:D}", viewModel.ReviewPreviewRows.Single().Values[0]);
    }

    [Fact]
    public async Task GenerateAsync_RecordsTheProfileNameCapturedBeforeTheRunStarted()
    {
        var viewModel = CreateViewModel(out var generationMock, out _, out var historyMock);
        viewModel.ActiveProfileName = "before-run";
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);

        var tcs = new TaskCompletionSource<GenerationResult>();
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var runTask = viewModel.GenerateCommand.ExecuteAsync(null);
        viewModel.ActiveProfileName = "loaded-mid-run";
        tcs.SetResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
        });
        await runTask;

        historyMock.Verify(h => h.AddRunAsync(
            It.Is<RunRecord>(r => r.Profile == "before-run"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenInBoard_during_a_run_applies_the_profile_when_the_run_ends()
    {
        // WR-010: ApplyImportReport returned early while running, and the import was dropped.
        var viewModel = CreateViewModel(out var generationMock, out _, out _);
        var result = new TaskCompletionSource<GenerationResult>();
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(result.Task);
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        var report = new ProfileImportReport(
            new Dictionary<string, Dictionary<string, FieldRule>>(),
            new Dictionary<string, int>(),
            null, 0, [], [], [], "imported-profile");

        var run = viewModel.GenerateCommand.ExecuteAsync(null);
        viewModel.ApplyImportReport(report);
        Assert.Equal("No profile loaded", viewModel.ActiveProfileName); // never under the live run

        result.SetResult(new GenerationResult());
        await run;

        Assert.Equal("imported-profile", viewModel.ActiveProfileName);
    }

    [Fact]
    public async Task Rules_saves_during_a_run_leave_the_board_alone_until_it_ends_on_both_routes()
    {
        // WR-009: Generate's Rules page (request.OnSaved) and the Profiles page's Rules save
        // (ApplySavedProfileIfActive) both renamed the profile and dropped tables under a live run.
        var result = new TaskCompletionSource<GenerationResult>();
        var generation = new Mock<IWpfGenerationService>();
        generation
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(result.Task);
        var history = new Mock<IRunHistoryService>();
        var request = new RulesNavigationRequest();
        var viewModel = new GenerateViewModel(
            Mock.Of<ISettingsService>(), Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(), Mock.Of<IMetadataProvider>(),
            Mock.Of<IProfileService>(), Mock.Of<IContentDialogService>(),
            new RunViewModel(generation.Object, history: history.Object),
            request,
            Mock.Of<IAppNavigator>());
        viewModel.OnEntitiesChanged(
        [
            new EntitySummary("account", "Account", false),
            new EntitySummary("contact", "Contact", false),
        ]);
        viewModel.ActiveProfileName = "contact-acct";
        viewModel.EditRulesCommand.Execute(null); // wires request.OnSaved, Generate's route
        var step = viewModel.CurrentStep;
        var saved = new Profile(2, "contact-acct", null, 42, [new ProfileTable("account", 10, null)]);

        var run = viewModel.GenerateCommand.ExecuteAsync(null);
        request.OnSaved!(saved);                     // Generate's Rules page
        viewModel.ApplySavedProfileIfActive(saved);  // Profiles' Rules page, same loaded profile

        Assert.Equal(["account", "contact"], viewModel.SelectedEntities.Select(e => e.LogicalName));
        Assert.Equal("contact-acct", viewModel.ActiveProfileName);
        Assert.Equal(step, viewModel.CurrentStep);

        result.SetResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
        });
        await run;

        Assert.Equal(["account"], viewModel.SelectedEntities.Select(e => e.LogicalName)); // applied at the end
        history.Verify(h => h.AddRunAsync(
            It.Is<RunRecord>(r => r.Profile == "contact-acct" && r.EntityNames.Length == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GoToReview_FlagsUnsuppliedRequiredLookup()
    {
        // CR-003: Review passed, then Start wrote the parent table and failed on the child.
        var required = new LookupAttributeMetadata { LogicalName = "new_requiredid", Targets = ["contact"], IsValidForCreate = true };
        required.GetType().GetProperty("RequiredLevel")!.SetValue(
            required, new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired));
        var viewModel = CreateViewModel(out _, out var metadataMock, out _);
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntityMetadata>)[BuildAccountMetadata(required)]);
        viewModel.AttachFieldRules(new FieldRulesViewModel());
        viewModel.OnEntitiesChanged([new EntitySummary("account", "Account", false)]);
        await viewModel.GoNextCommand.ExecuteAsync(null);

        viewModel.GoToReviewCommand.Execute(null);

        Assert.True(viewModel.ReviewHasErrors);
        Assert.Contains("new_requiredid", viewModel.ReviewErrorSummary, StringComparison.Ordinal);
        Assert.False(viewModel.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task GoToReview_AllowsRequiredLookupToSelectedTable()
    {
        // CR-003 amendment: contact's required lookup targets account, which this run creates first.
        var required = new LookupAttributeMetadata { LogicalName = "new_requiredid", Targets = ["account"], IsValidForCreate = true };
        required.GetType().GetProperty("RequiredLevel")!.SetValue(
            required, new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired));
        var contact = new EntityMetadata { LogicalName = "contact" };
        contact.GetType().GetProperty("Attributes")!.SetValue(contact, new AttributeMetadata[] { required });
        var viewModel = CreateViewModel(out _, out var metadataMock, out _);
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<EntityMetadata>)[BuildAccountMetadata(), contact]);
        viewModel.AttachFieldRules(new FieldRulesViewModel());
        viewModel.OnEntitiesChanged(
        [
            new EntitySummary("account", "Account", false),
            new EntitySummary("contact", "Contact", false),
        ]);
        await viewModel.GoNextCommand.ExecuteAsync(null);

        viewModel.GoToReviewCommand.Execute(null);

        Assert.False(viewModel.ReviewHasErrors);
        Assert.True(viewModel.GenerateCommand.CanExecute(null));
    }

    // EntityMetadata.Attributes setter is non-public — same reflection-set pattern used by
    // RuleEditorViewModelTests / SeedBomb.Bulk.Tests/RuledGenerationTests.
    private static EntityMetadata BuildAccountMetadata(params AttributeMetadata[] extra)
    {
        var name = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 100 };
        var employees = new IntegerAttributeMetadata { LogicalName = "numberofemployees", IsValidForCreate = true, MinValue = 0, MaxValue = 1000 };

        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, extra.Length == 0
            ? [name, employees]
            : extra.Concat<AttributeMetadata>([name, employees]).ToArray());
        return meta;
    }

    [Fact]
    public async Task EachStart_GetsItsOwnAlternateKeyScope()
    {
        var viewModel = await CreateReadyForRulesAsync(out var fieldRules, out var generationMock, out _, out _);
        fieldRules.SetRule("account", "name",
            new ConstantRule(System.Text.Json.JsonDocument.Parse("\"Acme\"").RootElement), "Name", "Acme");
        viewModel.GoToReviewCommand.Execute(null);
        var scopes = new List<string>();
        generationMock
            .Setup(g => g.GenerateAsync(It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Callback<GenerationConfig, IProgress<ProgressUpdate>, CancellationToken>((cfg, _, _) => scopes.Add(cfg.AlternateKeyScope))
            .ReturnsAsync(new GenerationResult());

        await viewModel.GenerateCommand.ExecuteAsync(null);
        await viewModel.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(2, scopes.Count);
        Assert.All(scopes, s => Assert.False(string.IsNullOrEmpty(s)));
        Assert.NotEqual(scopes[0], scopes[1]);
    }
}

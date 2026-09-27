using SeedBomb.Core.Exceptions;
using SeedBomb.Core.Metadata;
using SeedBomb.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using SeedBomb.Services.Dataverse;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.ViewModels;
using SeedBomb.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class RuleEditorViewModelTests
{
    private static StringAttributeMetadata StringColumn() =>
        new() { LogicalName = "name", IsValidForCreate = true, MaxLength = 20 };

    private static RuleEditorViewModel EditorFor(AttributeMetadata _)
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 42, runId: "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        return vm;
    }

    // EntityMetadata.Attributes/Keys setters are non-public — same reflection-set pattern
    // already used by SeedBomb.Bulk.Tests/RuledGenerationTests.cs to build SDK metadata fixtures.
    private static EntityMetadata BuildEntity()
    {
        var name = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 20 };
        var statecode = new StateAttributeMetadata { LogicalName = "statecode", IsValidForCreate = true };
        var statuscode = new StatusAttributeMetadata { LogicalName = "statuscode", IsValidForCreate = false };
        var exchangerate = new DecimalAttributeMetadata { LogicalName = "exchangerate", IsValidForCreate = false };
        var externalid = new StringAttributeMetadata { LogicalName = "externalid", IsValidForCreate = true, MaxLength = 50 };
        var overriddencreatedon = new DateTimeAttributeMetadata
        {
            LogicalName = "overriddencreatedon",
            IsValidForCreate = false,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
        };
        var ownerid = new LookupAttributeMetadata { LogicalName = "ownerid", IsValidForCreate = true, Targets = ["systemuser"] };
        var preferredcontactmethodcode = new MultiSelectPicklistAttributeMetadata { LogicalName = "preferredcontactmethodcode", IsValidForCreate = true };
        var numberofemployees = new IntegerAttributeMetadata { LogicalName = "numberofemployees", IsValidForCreate = true, MinValue = 0, MaxValue = 1_000_000 };

        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[]
        {
            name, statecode, statuscode, exchangerate, externalid, overriddencreatedon, ownerid, preferredcontactmethodcode, numberofemployees,
        });
        var altKey = new EntityKeyMetadata { LogicalName = "externalid_key", KeyAttributes = ["externalid"] };
        meta.GetType().GetProperty("Keys")!.SetValue(meta, new[] { altKey });
        return meta;
    }

    // 1. Picker: settable vs. platform-owned columns, with reasons (S3 acceptance surface).
    [Fact]
    public void Picker_lists_settable_and_excludes_platform_owned_columns_with_reasons()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 1, runId: "r1");

        Assert.Contains(vm.SettableColumns, c => c.LogicalName == "name");

        var excluded = vm.ExcludedColumns.ToDictionary(c => c.LogicalName, c => c);
        Assert.Equal(7, excluded.Count);

        Assert.False(excluded["statecode"].IsSelectable);
        Assert.Equal("Platform-owned state — set Status (reason) instead.", excluded["statecode"].DisabledReason);

        Assert.False(excluded["statuscode"].IsSelectable);
        Assert.False(string.IsNullOrWhiteSpace(excluded["statuscode"].DisabledReason));

        Assert.False(excluded["exchangerate"].IsSelectable);
        Assert.False(string.IsNullOrWhiteSpace(excluded["exchangerate"].DisabledReason));

        Assert.False(excluded["externalid"].IsSelectable);
        Assert.Contains("Alternate key", excluded["externalid"].DisabledReason);

        Assert.False(excluded["overriddencreatedon"].IsSelectable);
        Assert.False(string.IsNullOrWhiteSpace(excluded["overriddencreatedon"].DisabledReason));

        Assert.False(excluded["ownerid"].IsSelectable);
        Assert.Equal("Owner is assigned by Dataverse; owner rules are not supported.", excluded["ownerid"].DisabledReason);

        Assert.False(excluded["preferredcontactmethodcode"].IsSelectable);
        Assert.Contains("v2", excluded["preferredcontactmethodcode"].DisabledReason, StringComparison.OrdinalIgnoreCase);
    }

    // S3: excluded columns stay findable through search.
    [Fact]
    public void SearchText_filters_both_settable_and_excluded_groups()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 1, runId: "r1");

        vm.SearchText = "statecode";

        Assert.Empty(vm.SettableColumns);
        Assert.Single(vm.ExcludedColumns);
        Assert.Equal("statecode", vm.ExcludedColumns[0].LogicalName);
    }

    // 2. Selecting a column filters AvailableOps to ops valid for its type (§3.1 Applies-to).
    [Fact]
    public void AvailableOps_filters_by_selected_columns_type()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 1, runId: "r1");

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        Assert.Equal(["constant", "oneOf", "pattern", "null", "bogus"], vm.AvailableOps);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "numberofemployees");
        Assert.Equal(["constant", "oneOf", "range", "sequence", "null", "bogus"], vm.AvailableOps);
    }

    // 3. Routable-domain pattern warns but stays saveable (D3).
    [Fact]
    public void Routable_domain_pattern_warns_and_keeps_CanSave_true()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 1, runId: "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOp = "pattern";
        vm.Template = "dg{seq}@contoso.com";

        Assert.Contains(vm.Messages, m => m.Severity == RuleMessageSeverity.Warning);
        Assert.True(vm.CanSave);
    }

    // 4. Over-length constant errors and blocks save.
    [Fact]
    public void Overlength_constant_errors_and_blocks_CanSave()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 1, runId: "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name"); // MaxLength 20
        vm.SelectedOp = "constant";
        vm.ConstantText = new string('x', 30);

        Assert.Contains(vm.Messages, m => m.Severity == RuleMessageSeverity.Error);
        Assert.False(vm.CanSave);
    }

    // 5. Preview is engine-true: matches RuleValueGenerator.Evaluate for the same (seed, table, row, runId).
    [Fact]
    public async Task PreviewValues_match_RuleValueGenerator_Evaluate_for_rows_0_to_2()
    {
        var entity = BuildEntity();
        var attr = entity.Attributes.Single(a => a.LogicalName == "numberofemployees");
        var vm = new RuleEditorViewModel(entity, recordCount: 10, seed: 42, runId: "run1");

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "numberofemployees");
        vm.SelectedOp = "range";
        vm.MinText = "10";
        vm.MaxText = "500";

        var effective = vm.BuildRule();
        Assert.NotNull(effective);

        var expected = Enumerable.Range(0, 3)
            .Select(row => RuleValueGenerator.Evaluate(effective!, attr, 42, "account", row, "run1"))
            .Select(v => v!.ToString() ?? string.Empty)
            .ToArray();

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(expected, vm.PreviewValues);
    }

    // 6. null (platform default) is an available op for text columns and is saveable with no params.
    [Fact]
    public void Null_op_is_available_and_saveable_for_optional_text_column()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 1, runId: "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");

        Assert.Contains("null", vm.AvailableOps);
        Assert.Contains(vm.AvailableOpOptions, o => o.Op == "null" && o.Hint.Contains("default", StringComparison.OrdinalIgnoreCase));

        vm.SelectedOp = "null";

        Assert.True(vm.CanSave);
        Assert.IsType<NullRule>(vm.BuildRule());
    }

    // 7. Edit mode restores the existing rule op instead of always defaulting to constant.
    [Fact]
    public void ApplyExistingRule_restores_pattern_op_and_template()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 1, runId: "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        Assert.Equal("bogus", vm.SelectedOp); // type default after column select

        vm.ApplyExistingRule(new PatternRule("ACME-{seq:0000}"));

        Assert.Equal("pattern", vm.SelectedOp);
        Assert.Equal("ACME-{seq:0000}", vm.Template);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void ApiChangeWhileEndpointAlreadyNull_StillRevalidatesAndNotifies()
    {
        var vm = EditorFor(StringColumn());
        vm.SelectedOp = "bogus";
        var canSaveRaised = 0;
        vm.SaveProfileCommand.CanExecuteChanged += (_, _) => canSaveRaised++;

        vm.SelectedBogusApi = "NAME";        // endpoint was already null

        Assert.Null(vm.SelectedBogusEndpoint);
        Assert.True(canSaveRaised > 0);
        Assert.Contains(vm.GetErrors(nameof(vm.SelectedBogusEndpoint)).Cast<object>(), _ => true);
    }

    [Fact]
    public void Rule_level_errors_reach_the_InfoBar_but_not_the_Operation_field()
    {
        var vm = EditorFor(StringColumn());
        vm.SelectedOp = "pattern";   // Template empty => no draft => "Enter a value for this rule."

        Assert.Equal("Enter a value for this rule.", vm.InfoBarMessage);
        Assert.Empty(vm.GetErrors(null).Cast<string>());
        Assert.Empty(vm.GetErrors(nameof(vm.SelectedOp)).Cast<string>());
    }

    [Fact]
    public async Task ApplyExistingBogusRule_PreviewsThroughEvaluatorSession()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), recordCount: 10, seed: 42, runId: "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.ApplyExistingRule(new BogusRule("NAME", "firstName", 1));

        Assert.Equal("bogus", vm.SelectedOp);
        Assert.True(vm.CanSave);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal("Kurtis", vm.PreviewValues[0]);
        Assert.DoesNotContain(vm.Messages, m => m.Code == RuleMessageCode.ContextRequired);
    }

    [Fact]
    public void TemplateExpressionAliasesTemplate()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), 10, 1, "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOperation = "pattern";
        vm.TemplateExpression = "dg{seq}";
        Assert.Equal("dg{seq}", vm.Template);
        Assert.True(vm.IsTemplateOperation);
    }

    [Fact]
    public void TemplateExpressionNotifiesOnTemplateChange()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), 10, 1, "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOperation = "pattern";
        string? last = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.TemplateExpression))
                last = vm.TemplateExpression;
        };
        vm.Template = "dg{seq}";
        Assert.Equal("dg{seq}", last);
    }

    [Fact]
    public void MappedFilterPredicateKeepsRequiredUnmappedVisible()
    {
        var vm = new RuleEditorViewModel(BuildEntity(), 10, 1, "r1");
        vm.SetColumnFilterModeCommand.Execute(vm.ColumnFilterModes.Single(m => m.Key == "Mapped"));
        Assert.Null(vm.ColumnsView);
        Assert.Contains(vm.SettableColumns.Where(vm.MatchesColumnFilter), c => c.LogicalName == "name");
    }

    [Fact]
    public async Task LoadForProfile_from_generate_sets_breadcrumb_and_save_navigates_back()
    {
        var navigator = new Mock<IAppNavigator>();
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(1, "working-set", null, 42,
                [new ProfileTable("account", 50, null)]),
            TableName = "account",
            ReturnPage = typeof(GeneratePage),
        };
        var metadata = new Mock<IMetadataProvider>();
        metadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Microsoft.Xrm.Sdk.Metadata.EntityMetadata>());
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var vm = new RuleEditorViewModel(metadata.Object, profiles.Object, navigator.Object, request);
        await vm.LoadForProfileAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Generate", vm.BreadcrumbRootLabel);
        Assert.Equal("working-set", vm.ProfileName);

        vm.NavigateToProfilesCommand.Execute(null);
        navigator.Verify(n => n.Navigate(typeof(GeneratePage)), Times.Once);
    }

    [Fact]
    public async Task SelectedTable_change_reloads_columns_and_selects_first()
    {
        var account = BuildEntity();
        var firstname = new StringAttributeMetadata { LogicalName = "firstname", IsValidForCreate = true, MaxLength = 50 };
        var contact = new EntityMetadata { LogicalName = "contact" };
        contact.GetType().GetProperty("Attributes")!.SetValue(contact, new AttributeMetadata[] { firstname });

        var request = new RulesNavigationRequest
        {
            Profile = new Profile(2, "working-set", null, 42,
                [new ProfileTable("account", 10, null), new ProfileTable("contact", 10, null)]),
            TableName = "account",
        };
        var metadata = new Mock<IMetadataProvider>();
        metadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([account, contact]);

        var vm = new RuleEditorViewModel(
            metadata.Object,
            new Mock<IProfileService>().Object,
            new Mock<IAppNavigator>().Object,
            request);
        await vm.LoadForProfileAsync(TestContext.Current.CancellationToken);

        Assert.Equal("account", vm.SelectedTable?.LogicalName);
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        Assert.Contains("bogus", vm.AvailableOps);

        vm.SelectedTable = vm.Tables.Single(t => t.LogicalName == "contact");

        Assert.Equal("contact", vm.SelectedTable?.LogicalName);
        Assert.Contains(vm.SettableColumns, c => c.LogicalName == "firstname");
        Assert.DoesNotContain(vm.SettableColumns, c => c.LogicalName == "name");
        Assert.Equal("firstname", vm.SelectedColumn?.LogicalName);
        Assert.Contains("bogus", vm.AvailableOps);
    }

    // Repro for the crash-on-expired-connection bug: DataverseMetadataProvider wraps every
    // live-fetch failure (expired token included) as SchemaException. Today LoadForProfileAsync
    // has no try/catch around the fetch, so this exception escapes uncaught — RulesPage.xaml.cs's
    // OnNavigatedToAsync only catches OperationCanceledException, so it propagates all the way to
    // the WPF-UI navigation framework's async void dispatch and crashes the whole process.
    [Fact]
    public async Task LoadForProfileAsync_metadata_fetch_failure_does_not_throw()
    {
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(1, "working-set", null, 42,
                [new ProfileTable("account", 10, null)]),
            TableName = "account",
        };
        var metadata = new Mock<IMetadataProvider>();
        metadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SchemaException("Failed to retrieve metadata for entity 'account'.",
                new InvalidOperationException("expired token")));
        var snackbar = new Mock<ISnackbarService>();

        var vm = new RuleEditorViewModel(
            metadata.Object,
            new Mock<IProfileService>().Object,
            new Mock<IAppNavigator>().Object,
            request,
            snackbar: snackbar.Object,
            logger: Mock.Of<ILogger<RuleEditorViewModel>>());

        await vm.LoadForProfileAsync(TestContext.Current.CancellationToken);

        snackbar.Verify(
            s => s.Show(
                "Couldn't load table metadata",
                "Failed to retrieve metadata for entity 'account'.",
                ControlAppearance.Danger,
                null,
                It.IsAny<TimeSpan>()),
            Times.Once);
    }

    // Per-rule Save/Cancel/Delete (RulesPage Preview pane + config-panel "More" menu).
    private static async Task<(RuleEditorViewModel Vm, Mock<IAppNavigator> Navigator, Mock<IProfileService> Profiles, List<Profile> Saved)>
        LoadedEditorAsync(IReadOnlyList<string>? existingProfileNames = null, Type? returnPage = null,
            Dictionary<string, FieldRule>? nameColumnRule = null, string profileName = "working-set")
    {
        var navigator = new Mock<IAppNavigator>();
        var saved = new List<Profile>();
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(1, profileName, null, 42,
                [new ProfileTable("account", 10, nameColumnRule)]),
            TableName = "account",
            ReturnPage = returnPage,
            OnSaved = p => saved.Add(p),
        };
        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildEntity()]);
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingProfileNames ?? Array.Empty<string>());
        profiles.Setup(p => p.SaveAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var vm = new RuleEditorViewModel(metadata.Object, profiles.Object, navigator.Object, request);
        await vm.LoadForProfileAsync(TestContext.Current.CancellationToken);
        return (vm, navigator, profiles, saved);
    }

    private static async Task<(RuleEditorViewModel Vm, Mock<IProfileService> Profiles)> ReadyEditorAsync()
    {
        var existing = new ConstantRule(System.Text.Json.JsonSerializer.SerializeToElement("original"));
        var (vm, _, profiles, _) = await LoadedEditorAsync(
            existingProfileNames: ["working-set"],
            nameColumnRule: new Dictionary<string, FieldRule> { ["name"] = existing });
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.ConfirmDeleteRule = _ => Task.FromResult(true);
        return (vm, profiles);
    }

    [Fact]
    public async Task SaveRuleCommand_commits_without_navigating()
    {
        var (vm, navigator, _, saved) = await LoadedEditorAsync(returnPage: typeof(GeneratePage));
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOp = "constant";
        vm.ConstantText = "fixed";

        await vm.SaveRuleCommand.ExecuteAsync(null);

        Assert.Single(saved);
        Assert.True(saved[0].Tables.Single(t => t.Table == "account").Columns!.ContainsKey("name"));
        navigator.Verify(n => n.Navigate(It.IsAny<Type>()), Times.Never);
        Assert.Equal("name", vm.SelectedColumn?.LogicalName); // staleness-fix regression
    }

    [Fact]
    public async Task SaveProfileCommand_still_navigates_after_commit()
    {
        var (vm, navigator, _, saved) = await LoadedEditorAsync(
            returnPage: typeof(GeneratePage), existingProfileNames: ["g2"], profileName: "g2");
        // Not a working-set snapshot, so it saves back to its own name without prompting.
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOp = "constant";
        vm.ConstantText = "fixed";

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Single(saved);
        Assert.Equal("g2", saved[0].Name);
        Assert.True(saved[0].Tables.Single(t => t.Table == "account").Columns!.ContainsKey("name"));
        navigator.Verify(n => n.Navigate(typeof(GeneratePage)), Times.Once);
    }

    [Fact]
    public async Task SaveProfileCommand_on_working_set_prompts_for_name_and_saves_as_new_profile()
    {
        var (vm, navigator, _, saved) = await LoadedEditorAsync(returnPage: typeof(GeneratePage));
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOp = "constant";
        vm.ConstantText = "fixed";
        vm.PromptProfileName = _ => Task.FromResult<string?>("new-profile");

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Single(saved);
        Assert.Equal("new-profile", saved[0].Name);
        Assert.True(saved[0].Tables.Single(t => t.Table == "account").Columns!.ContainsKey("name"));
        Assert.Equal("new-profile", vm.ProfileName);
        navigator.Verify(n => n.Navigate(typeof(GeneratePage)), Times.Once);
    }

    [Fact]
    public async Task SaveProfileAsCommand_cancelled_prompt_does_not_save_or_navigate()
    {
        var (vm, navigator, _, saved) = await LoadedEditorAsync(returnPage: typeof(GeneratePage));
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOp = "constant";
        vm.ConstantText = "fixed";
        vm.PromptProfileName = _ => Task.FromResult<string?>(null);

        await vm.SaveProfileAsCommand.ExecuteAsync(null);

        Assert.Empty(saved);
        navigator.Verify(n => n.Navigate(It.IsAny<Type>()), Times.Never);
    }

    [Fact]
    public async Task CancelRuleCommand_discards_unsaved_edit_on_mapped_column()
    {
        var existing = new ConstantRule(System.Text.Json.JsonSerializer.SerializeToElement("original"));
        var (vm, _, _, _) = await LoadedEditorAsync(
            nameColumnRule: new Dictionary<string, FieldRule> { ["name"] = existing });
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        Assert.Equal("original", vm.ConstantText);

        vm.ConstantText = "dirty";

        Assert.True(vm.CancelRuleCommand.CanExecute(null));
        vm.CancelRuleCommand.Execute(null);

        Assert.Equal("original", vm.ConstantText);
    }

    [Fact]
    public async Task CancelRuleCommand_resets_op_to_default_on_never_mapped_column()
    {
        var (vm, _, _, _) = await LoadedEditorAsync();
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        Assert.Equal("bogus", vm.SelectedOp);

        vm.SelectedOp = "pattern";
        vm.Template = "dirty-{seq}";

        Assert.True(vm.CancelRuleCommand.CanExecute(null));
        vm.CancelRuleCommand.Execute(null);

        Assert.Equal("bogus", vm.SelectedOp);
    }

    [Fact]
    public void Switching_column_clears_previous_bogus_selection()
    {
        var vm = EditorFor(StringColumn());
        vm.SelectedBogusApi = "NAME";
        vm.SelectedBogusEndpoint = "NAME.firstName";

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "numberofemployees");

        Assert.Equal("bogus", vm.SelectedOp);
        Assert.Null(vm.SelectedBogusApi);
        Assert.Null(vm.SelectedBogusEndpoint);
        Assert.NotEmpty(vm.BogusInput.BogusApis);
    }

    [Fact]
    public async Task DeleteRuleCommand_CanExecute_toggles_with_mapped_state()
    {
        var (vm, _, _, _) = await LoadedEditorAsync();
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        Assert.False(vm.DeleteRuleCommand.CanExecute(null));

        vm.SelectedOp = "constant";
        vm.ConstantText = "fixed";
        await vm.SaveRuleCommand.ExecuteAsync(null);

        Assert.True(vm.DeleteRuleCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteRuleCommand_removes_mapping_after_confirmation()
    {
        var existing = new ConstantRule(System.Text.Json.JsonSerializer.SerializeToElement("original"));
        var (vm, _, _, saved) = await LoadedEditorAsync(
            nameColumnRule: new Dictionary<string, FieldRule> { ["name"] = existing });
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.ConfirmDeleteRule = _ => Task.FromResult(true);

        await vm.DeleteRuleCommand.ExecuteAsync(null);

        Assert.Single(saved);
        Assert.False(saved[0].Tables.Single(t => t.Table == "account").Columns!.ContainsKey("name"));
        Assert.Equal(0, vm.ColumnFilterModes.Single(m => m.Key == "Mapped").Count);
        Assert.Equal("name", vm.SelectedColumn?.LogicalName);
    }

    [Fact]
    public async Task DeleteRuleCommand_noop_when_confirmation_declined()
    {
        var existing = new ConstantRule(System.Text.Json.JsonSerializer.SerializeToElement("original"));
        var (vm, _, _, saved) = await LoadedEditorAsync(
            nameColumnRule: new Dictionary<string, FieldRule> { ["name"] = existing });
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.ConfirmDeleteRule = _ => Task.FromResult(false);

        await vm.DeleteRuleCommand.ExecuteAsync(null);

        Assert.Empty(saved);
        Assert.True(vm.DeleteRuleCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteRuleCommand_persists_only_when_profile_already_in_store()
    {
        var existing = new ConstantRule(System.Text.Json.JsonSerializer.SerializeToElement("original"));
        var (vm, _, profiles, _) = await LoadedEditorAsync(
            existingProfileNames: Array.Empty<string>(),
            nameColumnRule: new Dictionary<string, FieldRule> { ["name"] = existing });
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.ConfirmDeleteRule = _ => Task.FromResult(true);

        await vm.DeleteRuleCommand.ExecuteAsync(null);
        profiles.Verify(p => p.SaveAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()), Times.Never);

        var existing2 = new ConstantRule(System.Text.Json.JsonSerializer.SerializeToElement("original"));
        var (vm2, _, profiles2, _) = await LoadedEditorAsync(
            existingProfileNames: ["working-set"],
            nameColumnRule: new Dictionary<string, FieldRule> { ["name"] = existing2 });
        vm2.SelectedColumn = vm2.SettableColumns.Single(c => c.LogicalName == "name");
        vm2.ConfirmDeleteRule = _ => Task.FromResult(true);

        await vm2.DeleteRuleCommand.ExecuteAsync(null);
        profiles2.Verify(p => p.SaveAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveRule_WhenStoreThrows_SetsMetadataErrorAndDoesNotPropagate()
    {
        var (vm, profiles) = await ReadyEditorAsync();
        profiles.Setup(p => p.SaveAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("profile file is locked"));

        await vm.SaveRuleCommand.ExecuteAsync(null);

        Assert.True(vm.HasMetadataError);
        Assert.Contains("profile file is locked", vm.MetadataError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRule_WhenStoreThrows_SetsMetadataErrorAndDoesNotPropagate()
    {
        var (vm, profiles) = await ReadyEditorAsync();
        profiles.Setup(p => p.SaveAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("profile is corrupt"));

        await vm.DeleteRuleCommand.ExecuteAsync(null);

        Assert.True(vm.HasMetadataError);
        Assert.Contains("profile is corrupt", vm.MetadataError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveTableCommand_drops_table_and_rules_then_shows_the_next_table()
    {
        var contact = new EntityMetadata { LogicalName = "contact" };
        contact.GetType().GetProperty("Attributes")!.SetValue(contact, new AttributeMetadata[]
        {
            new StringAttributeMetadata { LogicalName = "firstname", IsValidForCreate = true, MaxLength = 50 },
        });
        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildEntity(), contact]);
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["contact-acct"]);
        profiles.Setup(p => p.SaveAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var saved = new List<Profile>();
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(2, "contact-acct", null, 42,
            [
                new ProfileTable("account", 10, new Dictionary<string, FieldRule>
                {
                    ["name"] = new ConstantRule(System.Text.Json.JsonSerializer.SerializeToElement("Acme")),
                }),
                new ProfileTable("contact", 5, null),
            ]),
            TableName = "account",
            OnSaved = saved.Add,
        };
        var vm = new RuleEditorViewModel(metadata.Object, profiles.Object, Mock.Of<IAppNavigator>(), request)
        {
            ConfirmRemoveTable = _ => Task.FromResult(true),
        };
        await vm.LoadForProfileAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.RemoveTableCommand.CanExecute(null));

        await vm.RemoveTableCommand.ExecuteAsync(null);

        Assert.Equal(["contact"], Assert.Single(saved).Tables.Select(t => t.Table));
        profiles.Verify(p => p.SaveAsync(saved[0], It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["contact"], vm.Tables.Select(t => t.LogicalName));
        Assert.Equal("contact", vm.SelectedTable?.LogicalName);
        Assert.Equal("firstname", vm.SelectedColumn?.LogicalName);
        Assert.False(vm.RemoveTableCommand.CanExecute(null)); // the last table stays
    }

    [Fact]
    public async Task SaveRuleCommand_and_SaveProfileCommand_CanExecute_stay_in_sync()
    {
        var (vm, _, _, _) = await LoadedEditorAsync();
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name"); // MaxLength 20
        vm.SelectedOp = "constant";
        vm.ConstantText = new string('x', 30);

        Assert.False(vm.SaveRuleCommand.CanExecute(null));
        Assert.False(vm.SaveProfileCommand.CanExecute(null));

        vm.ConstantText = "fixed";

        Assert.True(vm.SaveRuleCommand.CanExecute(null));
        Assert.True(vm.SaveProfileCommand.CanExecute(null));
    }

    // RelayCommand doesn't auto-hook CommandManager.RequerySuggested, so a bound Button/MenuItem
    // only re-queries CanExecute when CanExecuteChanged actually fires — calling CanExecute(null)
    // directly (as the other tests above do) can't catch a missing notify call, since it always
    // re-evaluates the predicate fresh regardless of whether anything was ever raised.
    [Fact]
    public async Task SelectedColumnChange_notifies_CancelAndDeleteRuleCommands()
    {
        var (vm, _, _, _) = await LoadedEditorAsync();
        var cancelRaised = 0;
        var deleteRaised = 0;
        vm.CancelRuleCommand.CanExecuteChanged += (_, _) => cancelRaised++;
        vm.DeleteRuleCommand.CanExecuteChanged += (_, _) => deleteRaised++;

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");

        Assert.True(cancelRaised > 0);
        Assert.True(deleteRaised > 0);
    }

    [Fact]
    public void BooleanColumn_OffersOnlyRandomBool()
    {
        var flag = new BooleanAttributeMetadata { LogicalName = "donotemail", IsValidForCreate = true };
        var vm = EditorForExtra(flag);
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "donotemail");
        vm.SelectedOp = "bogus";

        Assert.Equal(["RANDOM"], vm.BogusInput.BogusApis);
        vm.SelectedBogusApi = "RANDOM";
        Assert.Contains(vm.BogusInput.BogusEndpoints, o => o.Id == "RANDOM.bool");
        Assert.DoesNotContain(vm.BogusInput.BogusEndpoints, o => o.Id != "RANDOM.bool");
    }

    [Fact]
    public void ChoiceAndStatus_NeverOfferBogus()
    {
        var options = new OptionSetMetadata();
        options.Options.Add(new OptionMetadata(1));
        var choice = new PicklistAttributeMetadata
        {
            LogicalName = "preferredmethod",
            IsValidForCreate = true,
            OptionSet = options,
        };
        var vm = EditorForExtra(choice);
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "preferredmethod");
        Assert.DoesNotContain("bogus", vm.AvailableOps);

        var statusVm = new RuleEditorViewModel(BuildEntity(), 10, 1, "r1");
        statusVm.SelectedColumn = statusVm.ExcludedColumns.Single(c => c.LogicalName == "statecode");
        Assert.DoesNotContain("bogus", statusVm.AvailableOps);
    }

    [Fact]
    public void GetErrors_MapsAllEightTargets()
    {
        var vm = EditorFor(StringColumn());
        vm.SelectedOp = "bogus";
        vm.SelectedBogusApi = "NAME";
        Assert.Contains(vm.GetErrors(nameof(vm.SelectedBogusEndpoint)).Cast<object>(), _ => true);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "numberofemployees");
        vm.SelectedOp = "bogus";
        vm.SelectedBogusApi = "RANDOM";
        vm.SelectedBogusEndpoint = "RANDOM.number";
        vm.BogusMinNumber = "10";
        vm.BogusMaxNumber = "1";
        Assert.Contains(vm.GetErrors(nameof(vm.BogusMaxNumber)).Cast<object>(), _ => true);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOp = "bogus";
        vm.SelectedBogusApi = "RANDOM";
        vm.SelectedBogusEndpoint = "RANDOM.digits";
        vm.BogusLengthText = "0";
        Assert.Contains(vm.GetErrors(nameof(vm.BogusLengthText)).Cast<object>(), _ => true);
    }

    [Fact]
    public void Restore_NumericLengthDate_KeepsAuthoredArguments()
    {
        var scheduled = new DateTimeAttributeMetadata { LogicalName = "scheduledon", IsValidForCreate = true };
        var vm = EditorForExtra(scheduled);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "numberofemployees");
        var numeric = new BogusRule("RANDOM", "number", 1, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["min"] = System.Text.Json.JsonSerializer.SerializeToElement(3),
            ["max"] = System.Text.Json.JsonSerializer.SerializeToElement(9),
        });
        vm.ApplyExistingRule(numeric);
        Assert.Equal("3", vm.BogusMinNumber);
        Assert.Equal("9", vm.BogusMaxNumber);
        Assert.True(vm.BogusInput.BogusHasNumericArgs);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        var length = new BogusRule("RANDOM", "digits", 1, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["length"] = System.Text.Json.JsonSerializer.SerializeToElement(8),
        });
        vm.ApplyExistingRule(length);
        Assert.Equal("8", vm.BogusLengthText);
        Assert.True(vm.BogusInput.BogusHasLengthArg);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "scheduledon");
        var dates = new BogusRule("DATE", "between", 1, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["min"] = System.Text.Json.JsonSerializer.SerializeToElement("2020-01-01"),
            ["max"] = System.Text.Json.JsonSerializer.SerializeToElement("2020-12-31"),
        });
        vm.ApplyExistingRule(dates);
        Assert.Equal(new DateTime(2020, 1, 1), vm.BogusMinDate);
        Assert.Equal(new DateTime(2020, 12, 31), vm.BogusMaxDate);
        Assert.True(vm.BogusInput.BogusHasDateArgs);
    }

    [Fact]
    public async Task StalePreview_DoesNotPublishAfterSupersedingEdit()
    {
        var vm = EditorFor(StringColumn());
        vm.SelectedOp = "pattern";
        vm.Template = "ACME-{seq:0000}";
        vm.SelectedOp = "constant";
        vm.ConstantText = "fixed";

        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(["fixed", "fixed", "fixed"], vm.PreviewValues);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void Customer_manual_append_keeps_stamped_target_when_selector_changes()
    {
        var vm = new RuleEditorViewModel(BuildEntityWithLookups(), 3, 42, "lookup-test");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentcustomerid");
        vm.SelectedOp = "oneOf";
        vm.Pick = OneOfPick.Cycle;
        vm.LookupInput.SelectedTarget = "account";
        vm.LookupInput.GuidText = "11111111-1111-1111-1111-111111111111";
        Assert.True(vm.LookupInput.AppendManualCommand.CanExecute(null));
        vm.LookupInput.AppendManualCommand.Execute(null);

        vm.LookupInput.SelectedTarget = "contact";
        Assert.Equal("account", vm.LookupInput.Records.Single().Entity);
        vm.LookupInput.GuidText = "22222222-2222-2222-2222-222222222222";
        vm.LookupInput.AppendManualCommand.Execute(null);

        Assert.Equal(["account", "contact"], vm.LookupInput.Records.Select(r => r.Entity).ToArray());
        var built = Assert.IsType<OneOfRule>(vm.BuildRule());
        Assert.Equal(OneOfPick.Cycle, built.Pick);
        Assert.Equal("account", LookupRuleValue.TryParse(built.Values[0], CustomerColumn(), out var first, out _) ? first!.Entity : null);
        Assert.Equal("contact", LookupRuleValue.TryParse(built.Values[1], CustomerColumn(), out var second, out _) ? second!.Entity : null);
    }

    [Fact]
    public void Invalid_second_guid_adds_nothing_and_blocks_save()
    {
        var vm = new RuleEditorViewModel(BuildEntityWithLookups(), 3, 42, "lookup-test");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentaccountid");
        vm.SelectedOp = "oneOf";
        vm.LookupInput.GuidText = "11111111-1111-1111-1111-111111111111, not-a-guid";
        vm.LookupInput.AppendManualCommand.Execute(null);

        Assert.Empty(vm.LookupInput.Records);
        Assert.Contains(vm.Messages, m => m.Text.Contains("GUID 2", StringComparison.Ordinal));
        Assert.False(vm.CanSave);

        vm.LookupInput.GuidText = "";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void Constant_oneOf_constant_preserves_selection_until_exactly_one()
    {
        var vm = new RuleEditorViewModel(BuildEntityWithLookups(), 3, 42, "lookup-test");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentaccountid");
        vm.SelectedOp = "constant";
        vm.LookupInput.GuidText = "11111111-1111-1111-1111-111111111111, 22222222-2222-2222-2222-222222222222";
        vm.LookupInput.AppendManualCommand.Execute(null);
        Assert.Equal(2, vm.LookupInput.Records.Count);
        Assert.False(vm.CanSave);
        Assert.Contains(vm.Messages, m => m.Text.Contains("exactly one", StringComparison.OrdinalIgnoreCase));

        vm.SelectedOp = "oneOf";
        Assert.Equal(2, vm.LookupInput.Records.Count);
        Assert.True(vm.CanSave);
        Assert.IsType<OneOfRule>(vm.BuildRule());

        vm.SelectedOp = "constant";
        Assert.Equal(2, vm.LookupInput.Records.Count);
        Assert.False(vm.CanSave);
        Assert.Null(vm.BuildRule());

        vm.LookupInput.RemoveCommand.Execute(vm.LookupInput.Records[1]);
        Assert.True(vm.CanSave);
        Assert.IsType<ConstantRule>(vm.BuildRule());
    }

    [Fact]
    public async Task Cancel_picker_CancelRule_and_table_switch_restore_saved_lookup_rule()
    {
        var original = new OneOfRule(
        [
            new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme").ToJson(),
            new LookupRuleValue("account", Guid.Parse("33333333-3333-3333-3333-333333333333"), "Beta").ToJson(),
        ], OneOfPick.Cycle);
        var picker = new Mock<ILookupRecordPicker>();
        picker
            .Setup(p => p.PickAsync(
                It.IsAny<LookupAttributeMetadata>(),
                It.IsAny<IReadOnlyList<LookupRuleValue>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<LookupRuleValue>?)null);
        var (vm, _, _, _) = await LoadedLookupEditorAsync(
            picker: picker.Object,
            columns: new Dictionary<string, FieldRule> { ["parentaccountid"] = original });

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentaccountid");
        Assert.Equal(2, vm.LookupInput.Records.Count);
        vm.LookupInput.GuidText = "44444444-4444-4444-4444-444444444444";
        await vm.PickLookupRecordsCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.LookupInput.Records.Count);
        Assert.Equal("44444444-4444-4444-4444-444444444444", vm.LookupInput.GuidText);

        vm.CancelRuleCommand.Execute(null);
        Assert.Equal("", vm.LookupInput.GuidText);
        var restored = Assert.IsType<OneOfRule>(vm.BuildRule());
        Assert.Equal(original.Values.Select(v => v.GetRawText()), restored.Values.Select(v => v.GetRawText()));

        vm.LookupInput.GuidText = "55555555-5555-5555-5555-555555555555";
        vm.SelectedTable = vm.Tables.Single(t => t.LogicalName == "contact");
        Assert.Equal("firstname", vm.SelectedColumn?.LogicalName);
        Assert.Empty(vm.LookupInput.Records);
        Assert.Empty(vm.LookupInput.Targets);
        Assert.False(vm.IsLookupColumn);
    }

    [Fact]
    public async Task Stale_picker_result_is_ignored_after_column_op_or_connection_change()
    {
        var saved = new ConstantRule(
            new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme").ToJson());
        var stale = new LookupRuleValue("account", Guid.Parse("99999999-9999-9999-9999-999999999999"), "Stale");
        var picker = new Mock<ILookupRecordPicker>();
        var pending = new TaskCompletionSource<IReadOnlyList<LookupRuleValue>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        picker
            .Setup(p => p.PickAsync(
                It.IsAny<LookupAttributeMetadata>(),
                It.IsAny<IReadOnlyList<LookupRuleValue>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var connection = new Mock<IDataverseConnectionService>();
        var (vm, _, _, _) = await LoadedLookupEditorAsync(
            picker: picker.Object,
            connection: connection.Object,
            columns: new Dictionary<string, FieldRule> { ["parentaccountid"] = saved });
        vm.Activate();
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentaccountid");
        Assert.Equal("constant", vm.SelectedOp);

        var pickTask = vm.PickLookupRecordsCommand.ExecuteAsync(null);
        vm.SelectedOp = "oneOf";
        pending.SetResult([stale, new LookupRuleValue("account", Guid.Parse("88888888-8888-8888-8888-888888888888"))]);
        await pickTask;
        Assert.Equal("11111111-1111-1111-1111-111111111111", vm.LookupInput.Records.Single().Id.ToString());

        pending = new TaskCompletionSource<IReadOnlyList<LookupRuleValue>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        picker
            .Setup(p => p.PickAsync(
                It.IsAny<LookupAttributeMetadata>(),
                It.IsAny<IReadOnlyList<LookupRuleValue>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        vm.SelectedOp = "constant";
        pickTask = vm.PickLookupRecordsCommand.ExecuteAsync(null);
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentcustomerid");
        pending.SetResult([stale]);
        await pickTask;
        Assert.Empty(vm.LookupInput.Records);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentaccountid");
        pending = new TaskCompletionSource<IReadOnlyList<LookupRuleValue>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        picker
            .Setup(p => p.PickAsync(
                It.IsAny<LookupAttributeMetadata>(),
                It.IsAny<IReadOnlyList<LookupRuleValue>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        pickTask = vm.PickLookupRecordsCommand.ExecuteAsync(null);
        connection.Raise(c => c.ConnectionReset += null, connection.Object, EventArgs.Empty);
        pending.SetResult([stale]);
        await pickTask;
        Assert.Equal("11111111-1111-1111-1111-111111111111", vm.LookupInput.Records.Single().Id.ToString());
        Assert.False(vm.IsMetadataAvailable);
        Assert.False(vm.CanSave);
        Assert.Contains("Reconnect and retry", vm.MetadataError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionReset_ignores_ui_queued_picker_and_metadata_continuations()
    {
        var savedId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var saved = new ConstantRule(new LookupRuleValue("account", savedId, "Acme").ToJson());
        var stale = new LookupRuleValue("account", Guid.Parse("99999999-9999-9999-9999-999999999999"), "Stale");
        var picker = new Mock<ILookupRecordPicker>();
        var pickPending = new TaskCompletionSource<IReadOnlyList<LookupRuleValue>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        picker
            .Setup(p => p.PickAsync(
                It.IsAny<LookupAttributeMetadata>(),
                It.IsAny<IReadOnlyList<LookupRuleValue>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(pickPending.Task);

        var liveMeta = BuildEntityWithLookups();
        var staleColumn = new StringAttributeMetadata
        {
            LogicalName = "stale_column",
            IsValidForCreate = true,
            MaxLength = 20,
        };
        var staleMeta = BuildEntityWithLookups();
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.Attributes))!
            .SetValue(staleMeta, staleMeta.Attributes.Append(staleColumn).ToArray());

        var metadataPending = new TaskCompletionSource<IReadOnlyList<EntityMetadata>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataCalls = 0;
        var metadata = new Mock<IMetadataProvider>();
        metadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                metadataCalls++;
                return metadataCalls == 1
                    ? Task.FromResult<IReadOnlyList<EntityMetadata>>([liveMeta])
                    : metadataPending.Task;
            });

        var connection = new Mock<IDataverseConnectionService>();
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(2, "working-set", null, 42,
                [new ProfileTable("account", 10, new Dictionary<string, FieldRule> { ["parentaccountid"] = saved })]),
            TableName = "account",
        };
        var vm = new RuleEditorViewModel(
            metadata.Object,
            new Mock<IProfileService>().Object,
            new Mock<IAppNavigator>().Object,
            request,
            picker: picker.Object,
            connection: connection.Object);
        await vm.LoadForProfileAsync(TestContext.Current.CancellationToken);

        var sc = new QueueSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(sc);
        try
        {
            vm.Activate();
            vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentaccountid");

            var pickTask = vm.PickLookupRecordsCommand.ExecuteAsync(null);
            pickPending.SetResult([stale]);
            await WaitForQueuedAsync(sc, 1, TestContext.Current.CancellationToken);

            var retryTask = vm.RetryMetadataCommand.ExecuteAsync(null);
            metadataPending.SetResult([staleMeta]);
            await WaitForQueuedAsync(sc, 2, TestContext.Current.CancellationToken);

            connection.Raise(c => c.ConnectionReset += null, connection.Object, EventArgs.Empty);
            sc.Drain();
            await pickTask;
            await retryTask;

            Assert.Equal(savedId, vm.LookupInput.Records.Single().Id);
            Assert.DoesNotContain(vm.SettableColumns, c => c.LogicalName == "stale_column");
            Assert.False(vm.IsMetadataAvailable);
            Assert.Contains("Reconnect and retry", vm.MetadataError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            vm.Deactivate();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task Missing_profile_or_not_ready_connection_then_Retry_reloads_retained_profile()
    {
        var metadata = new Mock<IMetadataProvider>();
        var missingRequest = new RulesNavigationRequest();
        var missing = new RuleEditorViewModel(
            metadata.Object,
            new Mock<IProfileService>().Object,
            new Mock<IAppNavigator>().Object,
            missingRequest);
        missing.Activate();
        await missing.LoadForProfileAsync(TestContext.Current.CancellationToken);
        Assert.False(missing.IsMetadataAvailable);
        Assert.False(string.IsNullOrWhiteSpace(missing.MetadataError));
        Assert.False(missing.RetryMetadataCommand.CanExecute(null));

        var saved = new List<Profile>();
        var calls = 0;
        metadata
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                if (calls == 1)
                    return Task.FromException<IReadOnlyList<EntityMetadata>>(
                        new InvalidOperationException("No connection profile configured."));
                return Task.FromResult<IReadOnlyList<EntityMetadata>>([BuildEntityWithLookups()]);
            });
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(2, "working-set", null, 42,
                [new ProfileTable("account", 10, null)]),
            TableName = "account",
            OnSaved = p => saved.Add(p),
        };
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        var vm = new RuleEditorViewModel(
            metadata.Object,
            profiles.Object,
            new Mock<IAppNavigator>().Object,
            request);
        vm.Activate();
        await vm.LoadForProfileAsync(TestContext.Current.CancellationToken);
        Assert.False(vm.IsMetadataAvailable);
        Assert.Contains("No connection profile configured.", vm.MetadataError);
        Assert.True(vm.RetryMetadataCommand.CanExecute(null));

        await vm.RetryMetadataCommand.ExecuteAsync(null);
        Assert.True(vm.IsMetadataAvailable);
        Assert.True(string.IsNullOrWhiteSpace(vm.MetadataError));
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentaccountid");
        vm.SelectedOp = "lookupRandom";
        await vm.SaveRuleCommand.ExecuteAsync(null);
        Assert.Single(saved);
        Assert.IsType<LookupRandomRule>(saved[0].Tables[0].Columns!["parentaccountid"]);
    }

    [Fact]
    public async Task LookupRandom_editor_preview_is_explanatory_without_evaluate()
    {
        var vm = new RuleEditorViewModel(BuildEntityWithLookups(), 10, 42, "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentaccountid");
        vm.SelectedOp = "lookupRandom";

        Assert.True(vm.CanSave);
        Assert.IsType<LookupRandomRule>(vm.BuildRule());
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(vm.LookupRandomExplanation, Assert.Single(vm.PreviewValues));
        Assert.Contains("1,000", vm.PreviewValues[0], StringComparison.Ordinal);
        Assert.DoesNotContain("11111111", vm.PreviewValues[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Owner_rule_is_rejected_by_core_not_only_hidden_in_ui()
    {
        var meta = BuildEntity();
        var owner = meta.Attributes.Single(a => a.LogicalName == "ownerid");
        var rule = new ConstantRule(new LookupRuleValue("systemuser", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")).ToJson());
        var result = RuleValidator.Validate(rule, owner, new RuleValidationContext("account", 10, "r1"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Severity == RuleMessageSeverity.Error);
        Assert.DoesNotContain("constant", result.Messages[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Customer_oneof_reopens_with_mixed_identity_order_and_pick_mode()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "parentcustomerid",
            IsValidForCreate = true,
            Targets = ["account", "contact"],
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(attr, AttributeTypeCode.Customer);
        var meta = BuildEntity();
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.Attributes))!
            .SetValue(meta, meta.Attributes.Append(attr).ToArray());
        var vm = new RuleEditorViewModel(meta, 3, 42, "lookup-test");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "parentcustomerid");
        var original = new OneOfRule(new[]
        {
            new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme").ToJson(),
            new LookupRuleValue("contact", Guid.Parse("22222222-2222-2222-2222-222222222222"), "Alex").ToJson(),
        }, OneOfPick.Cycle);
        vm.ApplyExistingRule(original);
        Assert.Equal("oneOf", vm.SelectedOp);
        var reopened = Assert.IsType<OneOfRule>(vm.BuildRule());
        Assert.Equal(OneOfPick.Cycle, reopened.Pick);
        Assert.Equal(original.Values.Select(v => v.GetRawText()), reopened.Values.Select(v => v.GetRawText()));
    }

    private static LookupAttributeMetadata CustomerColumn()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "parentcustomerid",
            IsValidForCreate = true,
            Targets = ["account", "contact"],
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(attr, AttributeTypeCode.Customer);
        return attr;
    }

    private static LookupAttributeMetadata AccountLookupColumn()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid",
            IsValidForCreate = true,
            Targets = ["account"],
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(attr, AttributeTypeCode.Lookup);
        return attr;
    }

    private static EntityMetadata BuildEntityWithLookups()
    {
        var meta = BuildEntity();
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.Attributes))!
            .SetValue(meta, meta.Attributes.Concat([CustomerColumn(), AccountLookupColumn()]).ToArray());
        return meta;
    }

    private static async Task<(RuleEditorViewModel Vm, Mock<IAppNavigator> Navigator, Mock<IProfileService> Profiles, List<Profile> Saved)>
        LoadedLookupEditorAsync(
            ILookupRecordPicker? picker = null,
            IDataverseConnectionService? connection = null,
            Dictionary<string, FieldRule>? columns = null)
    {
        var navigator = new Mock<IAppNavigator>();
        var saved = new List<Profile>();
        var firstname = new StringAttributeMetadata { LogicalName = "firstname", IsValidForCreate = true, MaxLength = 50 };
        var contact = new EntityMetadata { LogicalName = "contact" };
        contact.GetType().GetProperty("Attributes")!.SetValue(contact, new AttributeMetadata[] { firstname });
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(2, "working-set", null, 42,
            [
                new ProfileTable("account", 10, columns),
                new ProfileTable("contact", 10, null),
            ]),
            TableName = "account",
            OnSaved = p => saved.Add(p),
        };
        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildEntityWithLookups(), contact]);
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        var vm = new RuleEditorViewModel(
            metadata.Object,
            profiles.Object,
            navigator.Object,
            request,
            picker: picker,
            connection: connection);
        await vm.LoadForProfileAsync(TestContext.Current.CancellationToken);
        return (vm, navigator, profiles, saved);
    }

    private static async Task WaitForQueuedAsync(QueueSynchronizationContext sc, int minCount, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sc.Count < minCount)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"UI context queued {sc.Count}, expected at least {minCount}.");
            await Task.Delay(10, ct);
        }
    }

    private sealed class QueueSynchronizationContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _queue = [];

        public int Count
        {
            get
            {
                lock (_queue)
                    return _queue.Count;
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
                _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Drain()
        {
            while (true)
            {
                SendOrPostCallback callback;
                object? state;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                        return;
                    (callback, state) = _queue[0];
                    _queue.RemoveAt(0);
                }

                callback(state);
            }
        }
    }

    private static RuleEditorViewModel EditorForExtra(params AttributeMetadata[] extra)
    {
        var name = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 20 };
        var employees = new IntegerAttributeMetadata { LogicalName = "numberofemployees", IsValidForCreate = true, MinValue = 0, MaxValue = 1_000_000 };
        var attrs = extra.Concat<AttributeMetadata>([name, employees]).ToArray();
        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, attrs);
        var vm = new RuleEditorViewModel(meta, 10, 42, "r1");
        return vm;
    }
}

using DataGen.Core.Exceptions;
using DataGen.Core.Metadata;
using DataGen.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Profiles;
using Seedbomb.ViewModels;
using Seedbomb.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace DataGen.Wpf.Tests;

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
    // already used by DataGen.Bulk.Tests/RuledGenerationTests.cs to build SDK metadata fixtures.
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
        Assert.Equal("Lookup — out of scope in v1.", excluded["ownerid"].DisabledReason);

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
        Assert.Equal("constant", vm.SelectedOp); // type default after column select

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
            Dictionary<string, FieldRule>? nameColumnRule = null)
    {
        var navigator = new Mock<IAppNavigator>();
        var saved = new List<Profile>();
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(1, "working-set", null, 42,
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
        var (vm, navigator, _, saved) = await LoadedEditorAsync(returnPage: typeof(GeneratePage));
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.SelectedOp = "constant";
        vm.ConstantText = "fixed";

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.Single(saved);
        Assert.True(saved[0].Tables.Single(t => t.Table == "account").Columns!.ContainsKey("name"));
        navigator.Verify(n => n.Navigate(typeof(GeneratePage)), Times.Once);
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
        Assert.Equal("constant", vm.SelectedOp);

        vm.SelectedOp = "pattern";
        vm.Template = "dirty-{seq}";

        Assert.True(vm.CancelRuleCommand.CanExecute(null));
        vm.CancelRuleCommand.Execute(null);

        Assert.Equal("constant", vm.SelectedOp);
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

        Assert.Equal(["RANDOM"], vm.BogusApis);
        vm.SelectedBogusApi = "RANDOM";
        Assert.Contains(vm.BogusEndpoints, o => o.Id == "RANDOM.bool");
        Assert.DoesNotContain(vm.BogusEndpoints, o => o.Id != "RANDOM.bool");
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
        Assert.True(vm.BogusHasNumericArgs);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        var length = new BogusRule("RANDOM", "digits", 1, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["length"] = System.Text.Json.JsonSerializer.SerializeToElement(8),
        });
        vm.ApplyExistingRule(length);
        Assert.Equal("8", vm.BogusLengthText);
        Assert.True(vm.BogusHasLengthArg);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "scheduledon");
        var dates = new BogusRule("DATE", "between", 1, new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["min"] = System.Text.Json.JsonSerializer.SerializeToElement("2020-01-01"),
            ["max"] = System.Text.Json.JsonSerializer.SerializeToElement("2020-12-31"),
        });
        vm.ApplyExistingRule(dates);
        Assert.Equal(new DateTime(2020, 1, 1), vm.BogusMinDate);
        Assert.Equal(new DateTime(2020, 12, 31), vm.BogusMaxDate);
        Assert.True(vm.BogusHasDateArgs);
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

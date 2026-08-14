using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class RuleEditorViewModelTests
{
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
        Assert.Equal(["constant", "oneOf", "pattern", "null"], vm.AvailableOps);

        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "numberofemployees");
        Assert.Equal(["constant", "oneOf", "range", "sequence", "null"], vm.AvailableOps);
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
    public void PreviewValues_match_RuleValueGenerator_Evaluate_for_rows_0_to_2()
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
}

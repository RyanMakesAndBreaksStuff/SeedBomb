// src/DataGen.Wpf.Tests/FieldRulesViewModelTests.cs
using DataGen.Core.Rules;
using Seedbomb.ViewModels.Controls;
using System.Text.Json;
using Xunit;

namespace Seedbomb.Tests;

public class FieldRulesViewModelTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Board_lists_only_active_rules()
    {
        var vm = new FieldRulesViewModel();
        vm.SelectTable("account");
        Assert.Empty(vm.Rows);                       // absence IS the auto configuration
        vm.SetRule("account", "description", new ConstantRule(J("\"[MARKER]\"")), displayName: "Description", preview: "[MARKER]");
        Assert.Single(vm.Rows);
        vm.RemoveRule("account", "description");
        Assert.Empty(vm.Rows);                       // column quietly returns to auto
    }

    [Fact]
    public void GetRules_returns_config_shape_and_empty_tables_are_omitted()
    {
        var vm = new FieldRulesViewModel();
        vm.SetRule("account", "description", new ConstantRule(J("\"old\"")), "Description", "old");
        var rules = vm.GetRules();
        Assert.Single(rules);
        Assert.Single(rules["account"]);
        vm.RemoveRule("account", "description");
        Assert.Empty(vm.GetRules());                 // {} table never emitted — keeps S7 trigger trivial
    }

    [Fact]
    public void Discard_restores_committed_state()
    {
        var vm = new FieldRulesViewModel();
        vm.SelectTable("account");
        vm.SetRule("account", "description", new ConstantRule(J("\"old\"")), "Description", "old preview");
        vm.Commit();
        vm.SetRule("account", "description", new ConstantRule(J("\"new\"")), "Changed label", "new preview");
        vm.SetRule("account", "revenue", new RangeRule(J("1"), J("2")), "Annual Revenue", "$1");
        vm.DiscardDraft();                            // S2: cancel restores prior committed verbatim
        Assert.Single(vm.GetRules()["account"]);
        var row = Assert.Single(vm.Rows);
        Assert.Equal("Description", row.DisplayName);
        Assert.Equal("old preview", row.Preview);
        Assert.Equal("old", Assert.IsType<ConstantRule>(row.Rule).Value.GetString());
    }

    [Fact]
    public void HardReset_then_DiscardDraft_does_not_restore_rules()
    {
        var vm = new FieldRulesViewModel();
        vm.SelectTable("account");
        vm.SetRule("account", "description", new ConstantRule(J("\"old\"")), "Description", "old");
        vm.Commit();
        var events = 0;
        vm.DraftChanged += (_, _) => events++;
        var revision = vm.Revision;

        vm.HardReset();

        Assert.Equal(1, events);
        Assert.True(vm.Revision > revision);
        Assert.False(vm.IsDirty);
        Assert.Empty(vm.GetRules());
        Assert.Empty(vm.Rows);

        vm.DiscardDraft();
        Assert.Empty(vm.GetRules());
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Every_draft_mutation_changes_revision()
    {
        var vm = new FieldRulesViewModel();
        var start = vm.Revision;
        vm.SetRule("account", "description", new ConstantRule(J("\"x\"")), "Description", "x");
        Assert.True(vm.Revision > start);
        var afterSet = vm.Revision;
        vm.RemoveRule("account", "description");
        Assert.True(vm.Revision > afterSet);
    }

    [Fact]
    public void OpBadge_and_Summary_display_bogus_and_lookup_identities()
    {
        var vm = new FieldRulesViewModel();
        vm.SelectTable("account");
        vm.SetRule("account", "name", new BogusRule("NAME", "firstName", 1), "Name", "preview");
        var bogus = Assert.Single(vm.Rows);
        Assert.Equal("bogus", bogus.OpBadge);
        Assert.Equal("NAME.firstName", bogus.ParameterSummary);

        vm.RemoveRule("account", "name");
        var named = new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme");
        var unnamed = new LookupRuleValue("contact", Guid.Parse("22222222-2222-2222-2222-222222222222"));
        vm.SetRule("account", "parentcustomerid",
            new OneOfRule([named.ToJson(), unnamed.ToJson()], OneOfPick.Cycle),
            "Customer", "preview");
        var identities = Assert.Single(vm.Rows);
        Assert.Equal("one-of", identities.OpBadge);
        Assert.DoesNotContain("\"entity\"", identities.ParameterSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", identities.ParameterSummary, StringComparison.Ordinal);
        Assert.Contains("Acme · account · 11111111-1111-1111-1111-111111111111", identities.ParameterSummary);
        Assert.Contains("contact · 22222222-2222-2222-2222-222222222222", identities.ParameterSummary);
        Assert.Contains("pick: cycle", identities.ParameterSummary);

        vm.RemoveRule("account", "parentcustomerid");
        vm.SetRule("account", "parentaccountid", new LookupRandomRule(), "Parent", "preview");
        var random = Assert.Single(vm.Rows);
        Assert.Equal("random lookup", random.OpBadge);
        Assert.Contains("1,000", random.ParameterSummary);
        Assert.DoesNotContain("?", random.OpBadge);
    }
}

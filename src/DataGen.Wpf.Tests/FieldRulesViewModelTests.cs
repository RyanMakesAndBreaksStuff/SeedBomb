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
}

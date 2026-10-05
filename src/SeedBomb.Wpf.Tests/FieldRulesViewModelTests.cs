// src/SeedBomb.Wpf.Tests/FieldRulesViewModelTests.cs
using SeedBomb.Core.Rules;
using SeedBomb.ViewModels.Controls;
using System.Text.Json;
using Xunit;

namespace SeedBomb.Tests;

public class FieldRulesViewModelTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Board_lists_only_active_rules()
    {
        var vm = new FieldRulesViewModel();
        Assert.Empty(vm.GetRules());                 // absence IS the auto configuration
        vm.SetRule("account", "description", new ConstantRule(J("\"[MARKER]\"")));
        Assert.Single(vm.GetRules());
        vm.ReplaceDraft(new Dictionary<string, Dictionary<string, RuleDraftEntry>>());
        Assert.Empty(vm.GetRules());                 // column quietly returns to auto
    }

    [Fact]
    public void GetRules_returns_config_shape_and_empty_tables_are_omitted()
    {
        var vm = new FieldRulesViewModel();
        vm.SetRule("account", "description", new ConstantRule(J("\"old\"")));
        var rules = vm.GetRules();
        Assert.Single(rules);
        Assert.Single(rules["account"]);
        vm.ReplaceDraft(new Dictionary<string, Dictionary<string, RuleDraftEntry>>());
        Assert.Empty(vm.GetRules());                 // {} table never emitted — keeps S7 trigger trivial
    }

    [Fact]
    public void HardReset_clears_draft_and_raises_one_change_event()
    {
        var vm = new FieldRulesViewModel();
        vm.SetRule("account", "description", new ConstantRule(J("\"old\"")));
        vm.Commit();
        var events = 0;
        vm.DraftChanged += (_, _) => events++;
        var revision = vm.Revision;

        vm.HardReset();

        Assert.Equal(1, events);
        Assert.True(vm.Revision > revision);
        Assert.False(vm.IsDirty);
        Assert.Empty(vm.GetRules());
    }

    [Fact]
    public void Every_draft_mutation_changes_revision()
    {
        var vm = new FieldRulesViewModel();
        var start = vm.Revision;
        vm.SetRule("account", "description", new ConstantRule(J("\"x\"")));
        Assert.True(vm.Revision > start);
        var afterSet = vm.Revision;
        vm.ReplaceDraft(new Dictionary<string, Dictionary<string, RuleDraftEntry>>());
        Assert.True(vm.Revision > afterSet);
    }
}

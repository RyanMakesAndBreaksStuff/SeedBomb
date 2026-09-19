using DataGen.Core.Rules;
using System.Text.Json;

namespace DataGen.Core.Tests.Rules;

public class RuleValueGeneratorTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Constant_choice_converts_to_OptionSetValue()
    {
        var attr = new PicklistAttributeMetadata { LogicalName = "accountcategorycode" };
        var v = RuleValueGenerator.Evaluate(new ConstantRule(J("1")), attr, seed: 42, table: "account", rowIndex: 0, runId: "r");
        Assert.Equal(1, Assert.IsType<OptionSetValue>(v).Value);
    }

    [Fact]
    public void Money_range_converts_to_Money_and_is_seed_stable()
    {
        var attr = new MoneyAttributeMetadata { LogicalName = "revenue", MinValue = 0, MaxValue = 1_000_000, PrecisionSource = 2, Precision = 2 };
        var rule = new RangeRule(J("10000"), J("250000"));
        var a = RuleValueGenerator.Evaluate(rule, attr, 42, "account", 5, "r");
        var b = RuleValueGenerator.Evaluate(rule, attr, 42, "account", 5, "r");
        var c = RuleValueGenerator.Evaluate(rule, attr, 42, "account", 6, "r");
        Assert.Equal(Assert.IsType<Money>(a).Value, Assert.IsType<Money>(b).Value);   // same (seed,row) ⇒ same value
        Assert.NotEqual(Assert.IsType<Money>(a).Value, Assert.IsType<Money>(c).Value); // row varies ⇒ value varies
        var m = Assert.IsType<Money>(a);
        Assert.InRange(m.Value, 10_000m, 250_000m);
    }

    [Fact]
    public void OneOf_cycle_indexes_by_row()
    {
        var attr = new PicklistAttributeMetadata { LogicalName = "rating" };
        var rule = new OneOfRule([J("1"), J("2")], OneOfPick.Cycle);
        Assert.Equal(1, ((OptionSetValue)RuleValueGenerator.Evaluate(rule, attr, 42, "account", 0, "r")!).Value);
        Assert.Equal(2, ((OptionSetValue)RuleValueGenerator.Evaluate(rule, attr, 42, "account", 1, "r")!).Value);
        Assert.Equal(1, ((OptionSetValue)RuleValueGenerator.Evaluate(rule, attr, 42, "account", 2, "r")!).Value);
    }

    [Fact]
    public void Pattern_seq_expands_per_row()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 };
        var v = RuleValueGenerator.Evaluate(new PatternRule("ACME-{seq:0000}"), attr, 42, "account", 0, "r");
        Assert.Equal("ACME-0001", v);
    }

    [Fact]
    public void Null_rule_returns_sentinel_omit()
    {
        var attr = new StringAttributeMetadata { LogicalName = "description" };
        Assert.Same(RuleValueGenerator.Omit, RuleValueGenerator.Evaluate(new NullRule(), attr, 42, "account", 0, "r"));
    }

    [Fact]
    public void Value_independent_of_evaluation_order()
    {
        var attr = new IntegerAttributeMetadata { LogicalName = "n", MinValue = 0, MaxValue = 1000 };
        var rule = new RangeRule(J("0"), J("1000"));
        // Evaluate rows out of order — row 3 must not depend on rows 0–2 having run.
        var outOfOrder = RuleValueGenerator.Evaluate(rule, attr, 42, "t", 3, "r");
        var fresh = RuleValueGenerator.Evaluate(rule, attr, 42, "t", 3, "r");
        Assert.Equal(fresh, outOfOrder);
    }
}

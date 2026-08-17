using System.Text.Json;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Tests.Rules;

public class RuleValidatorTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Range_outside_metadata_bounds_clamps_with_warning()
    {
        var attr = new IntegerAttributeMetadata { LogicalName = "numberofemployees", MinValue = 0, MaxValue = 1000 };
        var result = RuleValidator.Validate(new RangeRule(J("-50"), J("5000")), attr, recordCount: 10, runId: "r");
        Assert.True(result.IsValid);
        Assert.Contains(result.Messages, m => m.Severity == RuleMessageSeverity.Warning && m.Text.Contains("clamp", StringComparison.OrdinalIgnoreCase));
        var clamped = Assert.IsType<RangeRule>(result.EffectiveRule);
        Assert.Equal(0, clamped.Min.GetInt32());
        Assert.Equal(1000, clamped.Max.GetInt32());
    }

    [Fact]
    public void Pattern_expansion_beyond_MaxLength_rejects()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", MaxLength = 10 };
        var result = RuleValidator.Validate(new PatternRule("very-long-literal-{seq:0000}"), attr, 500, "r");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Choice_option_not_in_optionset_rejects()
    {
        var attr = PicklistWithOptions("accountratingcode", 1, 2);
        var result = RuleValidator.Validate(new ConstantRule(J("7")), attr, 10, "r");
        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Text.Contains("valid values", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Null_on_required_column_rejects()
    {
        var attr = new StringAttributeMetadata
        {
            LogicalName = "name",
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
        };
        Assert.False(RuleValidator.Validate(new NullRule(), attr, 10, "r").IsValid);
    }

    [Fact]
    public void Routable_email_domain_warns_but_stays_valid()
    {
        var attr = new StringAttributeMetadata { LogicalName = "emailaddress1", MaxLength = 100, Format = StringFormat.Email };
        var result = RuleValidator.Validate(new PatternRule("dg{seq}@contoso.com"), attr, 10, "r");
        Assert.True(result.IsValid);                       // D3: warn-only, never blocks
        Assert.Contains(result.Messages, m => m.Severity == RuleMessageSeverity.Warning);
    }

    [Fact]
    public void Sequence_overflow_past_metadata_max_is_error_not_clamp()
    {
        var attr = new IntegerAttributeMetadata { LogicalName = "n", MinValue = 0, MaxValue = 100 };
        Assert.False(RuleValidator.Validate(new SequenceRule(Start: 50, Step: 10), attr, recordCount: 10, runId: "r").IsValid);
    }

    [Fact]
    public void UnboundedDoubleColumn_DoesNotOverflow()
    {
        var attr = new DoubleAttributeMetadata { LogicalName = "d" }; // MinValue/MaxValue null
        var rule = new ConstantRule(JsonSerializer.SerializeToElement(1.5m));

        var result = RuleValidator.Validate(rule, attr, recordCount: 1, runId: "r");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void UnboundedMoneyColumn_DoesNotOverflow()
    {
        var attr = new MoneyAttributeMetadata { LogicalName = "m" };
        var rule = new ConstantRule(JsonSerializer.SerializeToElement(1.5m));

        var result = RuleValidator.Validate(rule, attr, recordCount: 1, runId: "r");

        Assert.True(result.IsValid);
    }

    private static PicklistAttributeMetadata PicklistWithOptions(string name, params int[] values)
    {
        var os = new OptionSetMetadata();
        foreach (var v in values) os.Options.Add(new OptionMetadata(v));
        return new PicklistAttributeMetadata { LogicalName = name, OptionSet = os };
    }
}

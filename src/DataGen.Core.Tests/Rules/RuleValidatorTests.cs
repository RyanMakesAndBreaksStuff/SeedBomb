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

    [Fact]
    public void ReversedNumericBounds_ReportMaximumTargetWithoutThrowing()
    {
        var attr = new IntegerAttributeMetadata { LogicalName = "n", MinValue = 0, MaxValue = 100 };
        var rule = new BogusRule("RANDOM", "number", 1, Args("min", 10, "max", 1));

        var result = RuleValidator.Validate(rule, attr,
            new RuleValidationContext(Table: "account", RecordCount: 10, RunId: "r"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Target == RuleInputTarget.Maximum
                                           && m.Code == RuleMessageCode.EmptyDomain);
    }

    [Fact]
    public void PositionalValidate_BogusRule_RequiresContext()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", MaxLength = 100 };
        var result = RuleValidator.Validate(new BogusRule("NAME", "firstName", 1), attr, 10, "r");
        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.ContextRequired);
    }

    [Fact]
    public void WrongValueKind_OnTextColumn_IsIncompatible()
    {
        var result = Validate(new BogusRule("RANDOM", "number", 1), StringAttr());
        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.Incompatible);
    }

    [Fact]
    public void UnknownEndpoint_IsRejected()
    {
        var result = Validate(new BogusRule("FOO", "bar", 1), StringAttr());
        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.UnknownEndpoint);
    }

    [Fact]
    public void UnknownArgument_IsRejected()
    {
        var result = Validate(new BogusRule("NAME", "firstName", 1, Args("foo", 1)), StringAttr());
        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.UnknownArgument);
    }

    [Fact]
    public void MissingDateArguments_AreReportedOnDateTargets()
    {
        var missingMin = Validate(new BogusRule("DATE", "between", 1, Args("max", "2020-01-02")), DateAttr());
        Assert.Contains(missingMin.Messages, m => m.Code == RuleMessageCode.MissingArgument
                                               && m.Target == RuleInputTarget.MinimumDate);

        var missingMax = Validate(new BogusRule("DATE", "between", 1, Args("min", "2020-01-01")), DateAttr());
        Assert.Contains(missingMax.Messages, m => m.Code == RuleMessageCode.MissingArgument
                                               && m.Target == RuleInputTarget.MaximumDate);
    }

    [Fact]
    public void OverflowingAndNonFiniteNumbers_DoNotThrow()
    {
        var overflow = Validate(
            new BogusRule("RANDOM", "number", 1, Args("min", JsonDocument.Parse("1e400").RootElement, "max", 1)),
            IntAttr());
        Assert.False(overflow.IsValid);
        Assert.Contains(overflow.Messages, m => m.Code == RuleMessageCode.Overflow);

        var badKind = Validate(
            new BogusRule("RANDOM", "number", 1, Args("min", "NaN", "max", 1)),
            IntAttr());
        Assert.False(badKind.IsValid);
        Assert.Contains(badKind.Messages, m => m.Code == RuleMessageCode.BadValueKind);
    }

    [Fact]
    public void OneSidedBounds_FillFromDescriptorDefaults()
    {
        var result = Validate(new BogusRule("RANDOM", "number", 1, Args("min", 10)), IntAttr(0, 100));
        Assert.True(result.IsValid);
        var effective = Assert.IsType<BogusRule>(result.EffectiveRule);
        Assert.Equal(10, effective.Args["min"].GetDecimal());
        Assert.Equal(100, effective.Args["max"].GetDecimal());
    }

    [Fact]
    public void FractionalIntegralBounds_AreQuantized()
    {
        var result = Validate(
            new BogusRule("RANDOM", "number", 1, Args("min", 1.2m, "max", 4.8m)),
            IntAttr(0, 100));
        Assert.True(result.IsValid);
        var effective = Assert.IsType<BogusRule>(result.EffectiveRule);
        Assert.Equal(2, effective.Args["min"].GetDecimal());
        Assert.Equal(4, effective.Args["max"].GetDecimal());
    }

    [Fact]
    public void NativeLimits_RejectAuthoredRangeOutsideEndpointDomain()
    {
        var result = Validate(
            new BogusRule("RANDOM", "byte", 1, Args("min", -1, "max", 300)),
            IntAttr(int.MinValue, int.MaxValue));
        Assert.True(result.IsValid);
        var effective = Assert.IsType<BogusRule>(result.EffectiveRule);
        Assert.Equal(0, effective.Args["min"].GetDecimal());
        Assert.Equal(255, effective.Args["max"].GetDecimal());
    }

    [Fact]
    public void NullMetadataBounds_DoNotOverflow_ForFloatingBogus()
    {
        var attr = new DoubleAttributeMetadata { LogicalName = "d" };
        var result = Validate(new BogusRule("RANDOM", "double", 1), attr);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void EmptyIntegralRange_AfterQuantize_IsEmptyDomain()
    {
        var result = Validate(
            new BogusRule("RANDOM", "number", 1, Args("min", 1.1m, "max", 1.2m)),
            IntAttr(0, 100));
        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.EmptyDomain);
    }

    [Fact]
    public void ParityEdges_EvenAndOdd()
    {
        var even = Validate(new BogusRule("RANDOM", "even", 1, Args("min", 1, "max", 3)), IntAttr(0, 100));
        Assert.True(even.IsValid);
        var evenRule = Assert.IsType<BogusRule>(even.EffectiveRule);
        Assert.Equal(2, evenRule.Args["min"].GetDecimal());
        Assert.Equal(2, evenRule.Args["max"].GetDecimal());

        var emptyEven = Validate(new BogusRule("RANDOM", "even", 1, Args("min", 1, "max", 1)), IntAttr(0, 100));
        Assert.False(emptyEven.IsValid);
        Assert.Contains(emptyEven.Messages, m => m.Code == RuleMessageCode.EmptyDomain);

        var odd = Validate(new BogusRule("RANDOM", "odd", 1, Args("min", 2, "max", 2)), IntAttr(0, 100));
        Assert.False(odd.IsValid);
        Assert.Contains(odd.Messages, m => m.Code == RuleMessageCode.EmptyDomain);
    }

    [Fact]
    public void NarrowLatitudeLongitudeAmountAndPort_UseCatalogDomains()
    {
        var lat = Validate(new BogusRule("ADDRESS", "latitude", 1),
            new DoubleAttributeMetadata { LogicalName = "lat", MinValue = 0, MaxValue = 10 });
        Assert.True(lat.IsValid);

        var emptyLat = Validate(new BogusRule("ADDRESS", "latitude", 1),
            new DoubleAttributeMetadata { LogicalName = "lat", MinValue = 100, MaxValue = 200 });
        Assert.False(emptyLat.IsValid);
        Assert.Contains(emptyLat.Messages, m => m.Code == RuleMessageCode.EmptyDomain);

        var lng = Validate(new BogusRule("ADDRESS", "longitude", 1),
            new DoubleAttributeMetadata { LogicalName = "lng", MinValue = -10, MaxValue = 10 });
        Assert.True(lng.IsValid);

        var amount = Validate(new BogusRule("FINANCE", "amount", 1),
            new MoneyAttributeMetadata { LogicalName = "rev", MinValue = 0, MaxValue = 50 });
        Assert.True(amount.IsValid);

        var port = Validate(new BogusRule("INTERNET", "port", 1), IntAttr(0, 100));
        Assert.False(port.IsValid);
        Assert.Contains(port.Messages, m => m.Code == RuleMessageCode.Incompatible);

        var portOk = Validate(new BogusRule("INTERNET", "port", 1), IntAttr());
        Assert.True(portOk.IsValid);
    }

    [Fact]
    public void HostileLengths_AreRejectedBeforeAllocation()
    {
        var tooLong = Validate(new BogusRule("RANDOM", "digits", 1, Args("length", 65_537)), StringAttr(100_000));
        Assert.False(tooLong.IsValid);
        Assert.Contains(tooLong.Messages, m => m.Code == RuleMessageCode.LengthBudget);

        var zero = Validate(new BogusRule("RANDOM", "digits", 1, Args("length", 0)), StringAttr());
        Assert.False(zero.IsValid);
        Assert.Contains(zero.Messages, m => m.Code == RuleMessageCode.LengthBudget);

        var bytes = Validate(new BogusRule("RANDOM", "bytes", 1, Args("length", 40_000)), StringAttr(100_000));
        Assert.False(bytes.IsValid);
        Assert.Contains(bytes.Messages, m => m.Code == RuleMessageCode.LengthBudget);

        var ean = Validate(new BogusRule("COMMERCE", "ean8", 1), StringAttr(5));
        Assert.False(ean.IsValid);
        Assert.Contains(ean.Messages, m => m.Code == RuleMessageCode.LengthBudget);
    }

    [Fact]
    public void DateBoundaries_0001_1752_ExactMinMax_AndReversed()
    {
        var tooEarly = Validate(new BogusRule("DATE", "between", 1, Args("min", "0001-01-01", "max", "2020-01-01")), DateAttr());
        Assert.False(tooEarly.IsValid);
        Assert.Contains(tooEarly.Messages, m => m.Code == RuleMessageCode.DateRange);

        var year1752 = Validate(new BogusRule("DATE", "between", 1, Args("min", "1752-12-31", "max", "2020-01-01")), DateAttr());
        Assert.False(year1752.IsValid);
        Assert.Contains(year1752.Messages, m => m.Code == RuleMessageCode.DateRange);

        var min = DateOnly.FromDateTime(DateTimeAttributeMetadata.MinSupportedValue).ToString("yyyy-MM-dd");
        var max = DateOnly.FromDateTime(DateTimeAttributeMetadata.MaxSupportedValue).ToString("yyyy-MM-dd");
        var exact = Validate(new BogusRule("DATE", "between", 1, Args("min", min, "max", max)), DateAttr());
        Assert.True(exact.IsValid);

        var reversed = Validate(new BogusRule("DATE", "between", 1, Args("min", "2020-12-31", "max", "2020-01-01")), DateAttr());
        Assert.False(reversed.IsValid);
        Assert.Contains(reversed.Messages, m => m.Code == RuleMessageCode.DateRange
                                             && m.Target == RuleInputTarget.MaximumDate);
    }

    [Fact]
    public void RiskyDescriptor_WarnsButRemainsValid_ExampleEmailDoesNot()
    {
        var email = Validate(new BogusRule("INTERNET", "email", 1), StringAttr());
        Assert.True(email.IsValid);
        Assert.Contains(email.Messages, m => m.Code == RuleMessageCode.RiskWarning);

        var example = Validate(new BogusRule("INTERNET", "exampleEmail", 1), StringAttr());
        Assert.True(example.IsValid);
        Assert.DoesNotContain(example.Messages, m => m.Code == RuleMessageCode.RiskWarning);
    }

    [Fact]
    public void UnsupportedEngineVersion_IsRejected()
    {
        var result = Validate(new BogusRule("NAME", "firstName", 2), StringAttr());
        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.EngineVersion);
    }

    private static PicklistAttributeMetadata PicklistWithOptions(string name, params int[] values)
    {
        var os = new OptionSetMetadata();
        foreach (var v in values) os.Options.Add(new OptionMetadata(v));
        return new PicklistAttributeMetadata { LogicalName = name, OptionSet = os };
    }

    private static RuleValidationResult Validate(FieldRule rule, AttributeMetadata attr, int recordCount = 10) =>
        RuleValidator.Validate(rule, attr, new RuleValidationContext("account", recordCount, "r"));

    private static StringAttributeMetadata StringAttr(int maxLength = 200) =>
        new() { LogicalName = "name", MaxLength = maxLength };

    private static IntegerAttributeMetadata IntAttr(int min = int.MinValue, int max = int.MaxValue) =>
        new() { LogicalName = "n", MinValue = min, MaxValue = max };

    private static DateTimeAttributeMetadata DateAttr() =>
        new() { LogicalName = "createdon" };

    private static Dictionary<string, JsonElement> Args(params object[] pairs)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            map[(string)pairs[i]] = pairs[i + 1] switch
            {
                JsonElement el => el.Clone(),
                _ => JsonSerializer.SerializeToElement(pairs[i + 1]),
            };
        }

        return map;
    }
}

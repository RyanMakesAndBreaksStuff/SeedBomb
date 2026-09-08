using System.Text.Json;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xunit;

namespace DataGen.Core.Tests.Rules;

public sealed class LookupRuleTests
{
    private static readonly RuleValidationContext Ctx = new("contact", 3, "lookup-test");
    private static readonly Guid RecordId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Lookup_constant_validates_and_evaluates_to_reference()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid", Targets = ["account"],
            IsValidForCreate = true,
        };
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var value = JsonSerializer.SerializeToElement(new { entity = "ACCOUNT", id });
        var rule = new ConstantRule(value);
        var result = RuleValidator.Validate(rule, attr,
            new RuleValidationContext("contact", 3, "lookup-test"));

        Assert.True(result.IsValid, string.Join(" ", result.Messages.Select(m => m.Text)));
        var reference = Assert.IsType<EntityReference>(RuleValueGenerator.Evaluate(
            result.EffectiveRule!, attr, 42, "contact", 0, "lookup-test"));
        Assert.Equal("account", reference.LogicalName);
        Assert.Equal(id, reference.Id);
    }

    [Fact]
    public void Bare_guid_string_is_validation_error_not_text_conversion()
    {
        var attr = SingleTargetLookup();
        var rule = new ConstantRule(JsonSerializer.SerializeToElement(RecordId.ToString("D")));
        AssertInvalid(rule, attr);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleValueGenerator.Evaluate(rule, attr, 42, "contact", 0, "lookup-test"));
        Assert.Contains("Invalid lookup value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nonobject_constant_is_validation_error()
        => AssertInvalid(new ConstantRule(JsonSerializer.SerializeToElement(1)), SingleTargetLookup());

    [Fact]
    public void Missing_entity_is_validation_error()
        => AssertInvalid(new ConstantRule(Parse("""{"id":"11111111-1111-1111-1111-111111111111"}""")), SingleTargetLookup());

    [Fact]
    public void Missing_id_is_validation_error()
        => AssertInvalid(new ConstantRule(Parse("""{"entity":"account"}""")), SingleTargetLookup());

    [Fact]
    public void Wrong_type_entity_is_validation_error()
        => AssertInvalid(new ConstantRule(Parse("""{"entity":1,"id":"11111111-1111-1111-1111-111111111111"}""")), SingleTargetLookup());

    [Fact]
    public void Wrong_type_id_is_validation_error()
        => AssertInvalid(new ConstantRule(Parse("""{"entity":"account","id":1}""")), SingleTargetLookup());

    [Fact]
    public void Invalid_guid_is_validation_error()
        => AssertInvalid(new ConstantRule(Parse("""{"entity":"account","id":"not-a-guid"}""")), SingleTargetLookup());

    [Fact]
    public void Empty_guid_is_validation_error()
        => AssertInvalid(new ConstantRule(Parse("""{"entity":"account","id":"00000000-0000-0000-0000-000000000000"}""")), SingleTargetLookup());

    [Fact]
    public void Wrong_target_is_validation_error()
        => AssertInvalid(new ConstantRule(Parse("""{"entity":"contact","id":"11111111-1111-1111-1111-111111111111"}""")), SingleTargetLookup());

    [Fact]
    public void Duplicate_json_keys_are_validation_error()
        => AssertInvalid(new ConstantRule(Parse(
            """{"entity":"account","id":"11111111-1111-1111-1111-111111111111","entity":"contact"}""")),
            SingleTargetLookup());

    [Fact]
    public void Customer_oneOf_cycle_preserves_input_order_and_identities()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "customerid",
            Targets = ["account", "contact"],
            IsValidForCreate = true,
        };
        SetAttributeType(attr, AttributeTypeCode.Customer);
        var accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var contactId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var rule = new OneOfRule(
        [
            JsonSerializer.SerializeToElement(new { entity = "account", id = accountId }),
            JsonSerializer.SerializeToElement(new { entity = "CONTACT", id = contactId }),
            JsonSerializer.SerializeToElement(new { entity = "account", id = accountId }),
        ], OneOfPick.Cycle);

        var result = RuleValidator.Validate(rule, attr, Ctx);
        Assert.True(result.IsValid, string.Join(" ", result.Messages.Select(m => m.Text)));
        var oneOf = Assert.IsType<OneOfRule>(result.EffectiveRule);
        Assert.Equal(3, oneOf.Values.Count);
        Assert.Equal("account", oneOf.Values[0].GetProperty("entity").GetString());
        Assert.Equal(accountId.ToString("D"), oneOf.Values[0].GetProperty("id").GetString());
        Assert.Equal("contact", oneOf.Values[1].GetProperty("entity").GetString());
        Assert.Equal(contactId.ToString("D"), oneOf.Values[1].GetProperty("id").GetString());
        Assert.Equal("account", oneOf.Values[2].GetProperty("entity").GetString());

        var row0 = Assert.IsType<EntityReference>(RuleValueGenerator.Evaluate(oneOf, attr, 42, "contact", 0, "lookup-test"));
        var row1 = Assert.IsType<EntityReference>(RuleValueGenerator.Evaluate(oneOf, attr, 42, "contact", 1, "lookup-test"));
        var row2 = Assert.IsType<EntityReference>(RuleValueGenerator.Evaluate(oneOf, attr, 42, "contact", 2, "lookup-test"));
        Assert.Equal("account", row0.LogicalName);
        Assert.Equal(accountId, row0.Id);
        Assert.Equal("contact", row1.LogicalName);
        Assert.Equal(contactId, row1.Id);
        Assert.Equal("account", row2.LogicalName);
        Assert.Equal(accountId, row2.Id);
    }

    [Fact]
    public void Identity_with_and_without_name_share_generation_identity()
    {
        var attr = SingleTargetLookup();
        var without = RuleValidator.Validate(
            new ConstantRule(JsonSerializer.SerializeToElement(new { entity = "account", id = RecordId })),
            attr, Ctx);
        var withName = RuleValidator.Validate(
            new ConstantRule(JsonSerializer.SerializeToElement(new { entity = "account", id = RecordId, name = "Contoso" })),
            attr, Ctx);
        Assert.True(without.IsValid, string.Join(" ", without.Messages.Select(m => m.Text)));
        Assert.True(withName.IsValid, string.Join(" ", withName.Messages.Select(m => m.Text)));

        var a = Assert.IsType<EntityReference>(RuleValueGenerator.Evaluate(
            without.EffectiveRule!, attr, 42, "contact", 0, "lookup-test"));
        var b = Assert.IsType<EntityReference>(RuleValueGenerator.Evaluate(
            withName.EffectiveRule!, attr, 42, "contact", 0, "lookup-test"));
        Assert.Equal(a.LogicalName, b.LogicalName);
        Assert.Equal(a.Id, b.Id);
        Assert.Null(a.Name);
        Assert.Null(b.Name);
    }

    [Fact]
    public void LookupRandom_on_text_is_validation_error()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", MaxLength = 160, IsValidForCreate = true };
        AssertInvalid(new LookupRandomRule(), attr);
    }

    [Fact]
    public void Bogus_on_lookup_is_validation_error()
        => AssertInvalid(new BogusRule("NAME", "firstName", 1), SingleTargetLookup());

    [Fact]
    public void Pattern_on_lookup_is_validation_error()
        => AssertInvalid(new PatternRule("{seq}"), SingleTargetLookup());

    [Fact]
    public void Range_on_lookup_is_validation_error()
        => AssertInvalid(
            new RangeRule(JsonSerializer.SerializeToElement(1), JsonSerializer.SerializeToElement(2)),
            SingleTargetLookup());

    [Fact]
    public void Sequence_on_lookup_is_validation_error()
        => AssertInvalid(new SequenceRule(), SingleTargetLookup());

    [Fact]
    public void Null_on_application_required_lookup_is_validation_error()
    {
        var attr = SingleTargetLookup();
        attr.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired);
        AssertInvalid(new NullRule(), attr);
    }

    [Fact]
    public void Null_on_system_required_lookup_is_validation_error()
    {
        var attr = SingleTargetLookup();
        attr.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired);
        AssertInvalid(new NullRule(), attr);
    }

    [Fact]
    public void LookupRandom_deserializes_from_op()
    {
        var rule = JsonSerializer.Deserialize<FieldRule>("""{"op":"lookupRandom"}""", FieldRule.JsonOptions);
        Assert.IsType<LookupRandomRule>(rule);
    }

    [Fact]
    public void Evaluate_LookupRandom_without_prepared_candidates_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleValueGenerator.Evaluate(new LookupRandomRule(), SingleTargetLookup(), 42, "contact", 0, "lookup-test"));
        Assert.Contains("prepared candidates", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateLookupRandom_selects_deterministically_from_prepared_candidates()
    {
        var candidates = new LookupRuleValue[]
        {
            new("account", Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new("account", Guid.Parse("22222222-2222-2222-2222-222222222222")),
        };
        var a = RuleValueGenerator.EvaluateLookupRandom(candidates, 42, "contact", "parentaccountid", 0);
        var b = RuleValueGenerator.EvaluateLookupRandom(candidates, 42, "contact", "parentaccountid", 0);
        Assert.Equal(a.LogicalName, b.LogicalName);
        Assert.Equal(a.Id, b.Id);
        Assert.Contains(candidates, c => c.Id == a.Id && c.Entity == a.LogicalName);
    }

    [Fact]
    public void EvaluateLookupRandom_empty_candidates_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleValueGenerator.EvaluateLookupRandom([], 42, "contact", "parentaccountid", 0));
        Assert.Contains("No prepared candidates", ex.Message, StringComparison.Ordinal);
    }

    private static LookupAttributeMetadata SingleTargetLookup() => new()
    {
        LogicalName = "parentaccountid",
        Targets = ["account"],
        IsValidForCreate = true,
    };

    private static void SetAttributeType(AttributeMetadata attr, AttributeTypeCode type)
        => typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(attr, type);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void AssertInvalid(FieldRule rule, AttributeMetadata attr)
    {
        var result = RuleValidator.Validate(rule, attr, Ctx);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Messages);
    }
}

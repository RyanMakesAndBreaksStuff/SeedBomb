using System.Text.Json;
using DataGen.Core.Rules;

namespace DataGen.Core.Tests.Rules;

public class FieldRuleSerializationTests
{
    private static readonly JsonSerializerOptions Options = FieldRule.JsonOptions;

    [Theory]
    [InlineData("""{"op":"constant","value":1}""", typeof(ConstantRule))]
    [InlineData("""{"op":"oneOf","values":[1,2],"pick":"random"}""", typeof(OneOfRule))]
    [InlineData("""{"op":"range","min":10000,"max":250000}""", typeof(RangeRule))]
    [InlineData("""{"op":"pattern","template":"dg+{seq:0000}@test.invalid"}""", typeof(PatternRule))]
    [InlineData("""{"op":"sequence","start":0,"step":1}""", typeof(SequenceRule))]
    [InlineData("""{"op":"null"}""", typeof(NullRule))]
    public void Deserializes_each_op(string json, Type expected)
    {
        var rule = JsonSerializer.Deserialize<FieldRule>(json, Options);
        Assert.IsType(expected, rule);
    }

    [Fact]
    public void Unknown_op_is_hard_error()
        => Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<FieldRule>("""{"op":"script","body":"x"}""", Options));

    [Fact]
    public void Unknown_property_is_hard_error()
        => Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<FieldRule>("""{"op":"null","unexpected":true}""", Options));

    [Fact]
    public void Round_trip_is_idempotent()
    {
        var rule = new OneOfRule([JsonDocument.Parse("1").RootElement, JsonDocument.Parse("2").RootElement], OneOfPick.Cycle);
        var json = JsonSerializer.Serialize<FieldRule>(rule, Options);
        var back = JsonSerializer.Deserialize<FieldRule>(json, Options);
        Assert.Equal(json, JsonSerializer.Serialize(back, Options));
    }
}

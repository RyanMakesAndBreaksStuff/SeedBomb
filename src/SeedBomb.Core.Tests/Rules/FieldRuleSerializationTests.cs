using SeedBomb.Core.Rules;
using System.Text.Json;

namespace SeedBomb.Core.Tests.Rules;

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
    [InlineData("""{"op":"lookupRandom"}""", typeof(LookupRandomRule))]
    public void Deserializes_each_op(string json, Type expected)
    {
        var rule = JsonSerializer.Deserialize<FieldRule>(json, Options);
        Assert.IsType(expected, rule);
    }

    [Fact]
    public void BogusRule_RoundTripsAsV2()
    {
        const string json = """
            {"op":"bogus","api":"RANDOM","endpoint":"number","engineVersion":1,"args":{"min":1,"max":10}}
            """;

        var rule = JsonSerializer.Deserialize<FieldRule>(json, Options);

        var bogus = Assert.IsType<BogusRule>(rule);
        Assert.Equal("RANDOM", bogus.Api);
        Assert.Equal("number", bogus.Endpoint);
        Assert.Equal(1, bogus.EngineVersion);
        Assert.Equal(1, bogus.Args["min"].GetInt32());
        Assert.Equal(10, bogus.Args["max"].GetInt32());

        var again = JsonSerializer.Deserialize<FieldRule>(
            JsonSerializer.Serialize<FieldRule>(bogus, Options), Options);
        var roundTripped = Assert.IsType<BogusRule>(again);
        Assert.Equal(bogus.Api, roundTripped.Api);
        Assert.Equal(bogus.Endpoint, roundTripped.Endpoint);
        Assert.Equal(bogus.EngineVersion, roundTripped.EngineVersion);
    }

    [Fact]
    public void Missing_engineVersion_is_rejected()
        => Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<FieldRule>(
                """{"op":"bogus","api":"RANDOM","endpoint":"number"}""", Options));

    [Fact]
    public void Args_survive_disposed_source_document()
    {
        BogusRule rule;
        using (var doc = JsonDocument.Parse("""{"min":1,"max":10}"""))
        {
            rule = new BogusRule("RANDOM", "number", 1, new Dictionary<string, JsonElement>
            {
                ["min"] = doc.RootElement.GetProperty("min"),
                ["max"] = doc.RootElement.GetProperty("max"),
            });
        }

        Assert.Equal(1, rule.Args["min"].GetInt32());
        Assert.Equal(10, rule.Args["max"].GetInt32());
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

    [Fact]
    public void LookupRandom_deserializes_as_marker()
    {
        var rule = JsonSerializer.Deserialize<FieldRule>("""{"op":"lookupRandom"}""", Options);
        Assert.IsType<LookupRandomRule>(rule);
        var json = JsonSerializer.Serialize<FieldRule>(new LookupRandomRule(), Options);
        Assert.IsType<LookupRandomRule>(JsonSerializer.Deserialize<FieldRule>(json, Options));
    }

    [Fact]
    public void Lookup_identity_without_name_round_trips()
    {
        const string json = """
            {"op":"constant","value":{"entity":"account","id":"11111111-1111-1111-1111-111111111111"}}
            """;

        var rule = Assert.IsType<ConstantRule>(JsonSerializer.Deserialize<FieldRule>(json, Options));
        Assert.Equal("account", rule.Value.GetProperty("entity").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", rule.Value.GetProperty("id").GetString());
        Assert.False(rule.Value.TryGetProperty("name", out _));

        var again = Assert.IsType<ConstantRule>(
            JsonSerializer.Deserialize<FieldRule>(JsonSerializer.Serialize<FieldRule>(rule, Options), Options));
        Assert.Equal("account", again.Value.GetProperty("entity").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", again.Value.GetProperty("id").GetString());
        Assert.False(again.Value.TryGetProperty("name", out _));
    }

    [Fact]
    public void Lookup_identity_with_name_round_trips()
    {
        var value = new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Contoso").ToJson();
        var rule = new ConstantRule(value);
        var json = JsonSerializer.Serialize<FieldRule>(rule, Options);
        var back = Assert.IsType<ConstantRule>(JsonSerializer.Deserialize<FieldRule>(json, Options));
        Assert.Equal("account", back.Value.GetProperty("entity").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", back.Value.GetProperty("id").GetString());
        Assert.Equal("Contoso", back.Value.GetProperty("name").GetString());
    }
}

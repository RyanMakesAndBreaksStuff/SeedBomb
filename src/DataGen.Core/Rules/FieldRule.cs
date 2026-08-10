using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataGen.Core.Rules;

/// <summary>
/// One allowlisted generation rule attached to a column. Discriminated by <c>op</c>;
/// unknown ops are a hard deserialization error (spec S4 — rules are data, never code).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ConstantRule), "constant")]
[JsonDerivedType(typeof(OneOfRule), "oneOf")]
[JsonDerivedType(typeof(RangeRule), "range")]
[JsonDerivedType(typeof(PatternRule), "pattern")]
[JsonDerivedType(typeof(SequenceRule), "sequence")]
[JsonDerivedType(typeof(NullRule), "null")]
public abstract record FieldRule
{
    /// <summary>Serializer options shared by profiles and config plumbing.</summary>
    public static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>Fixed value on every row. Value is typed per column at validation time (§3.1).</summary>
public sealed record ConstantRule(JsonElement Value) : FieldRule;

/// <summary>Pick from a literal subset. <c>cycle</c> = values[row % n]; <c>random</c> = per-record substream.</summary>
public sealed record OneOfRule(IReadOnlyList<JsonElement> Values, OneOfPick Pick = OneOfPick.Random) : FieldRule;

/// <summary>Uniform pick within [Min, Max]; DateTime columns carry ISO-8601 strings (§08).</summary>
public sealed record RangeRule(JsonElement Min, JsonElement Max) : FieldRule;

/// <summary>Text template of literals plus closed tokens {seq[:0N]}, {random:N}, {runId}.</summary>
public sealed record PatternRule(string Template) : FieldRule;

/// <summary>start + row·step; overflow past metadata max is a design-time error (never clamped).</summary>
public sealed record SequenceRule(decimal Start = 0, decimal Step = 1) : FieldRule;

/// <summary>Emit nothing; platform default applies. Invalid on required columns.</summary>
public sealed record NullRule : FieldRule;

/// <summary>Pick mode for <see cref="OneOfRule"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OneOfPick>))]
public enum OneOfPick
{
    /// <summary>Independent per-record substream pick.</summary>
    Random,

    /// <summary>Deterministic values[row % n] pick.</summary>
    Cycle
}

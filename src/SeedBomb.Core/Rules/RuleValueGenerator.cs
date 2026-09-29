using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SeedBomb.Core.Rules;

/// <summary>
/// Evaluates a validated rule for one (table, column, row) into an exact SDK payload (§3.4, S6/S8).
/// Randomized ops draw from a private substream keyed by (seed, table, column, rowIndex) — never from
/// the legacy per-entity Faker stream, so unruled columns stay byte-identical (S7) and values never
/// depend on batch size, parallelism, or retry order (S8).
/// </summary>
public static class RuleValueGenerator
{
    /// <summary>Sentinel: attribute must be omitted from the Entity (null rule → platform default).</summary>
    public static readonly object Omit = new();

    /// <summary>Evaluates one rule. Caller guarantees the rule passed <see cref="RuleValidator"/>.</summary>
    public static object? Evaluate(FieldRule rule, AttributeMetadata attr, int seed, string table, int rowIndex, string runId)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(attr);

        return rule switch
        {
            NullRule => Omit,
            ConstantRule c => Convert(c.Value, attr),
            OneOfRule o => Convert(Pick(o, seed, table, attr.LogicalName!, rowIndex), attr),
            SequenceRule s => ConvertNumber(s.Start + rowIndex * s.Step, attr),
            RangeRule r => EvaluateRange(r, attr, seed, table, rowIndex),
            PatternRule p => PatternTemplate.Parse(p.Template).Expand(
                rowIndex,
                n => RandomChars(seed, table, attr.LogicalName!, rowIndex, n),
                runId),
            BogusRule => throw new InvalidOperationException(
                "Bogus rules evaluate through BogusEvaluatorSession with a prepared rule."),
            LookupRandomRule => throw new InvalidOperationException(
                "LookupRandom rules require prepared candidates."),
            _ => throw new InvalidOperationException($"Unhandled rule type {rule.GetType().Name}"),
        };
    }

    /// <summary>Selects from a non-empty, canonically ordered prepared candidate list.</summary>
    /// <param name="candidates">Immutable identities from run preparation.</param>
    /// <param name="seed">Run seed.</param>
    /// <param name="table">Source table logical name.</param>
    /// <param name="column">Source lookup logical name.</param>
    /// <param name="rowIndex">Zero-based generated row.</param>
    public static EntityReference EvaluateLookupRandom(IReadOnlyList<LookupRuleValue> candidates,
        int seed, string table, string column, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(rowIndex);
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No prepared candidates for '{table}.{column}'.");
        var index = (int)(UniformDouble(seed, table, column, rowIndex) * candidates.Count);
        return candidates[index].ToReference();
    }

    // ── substream: HMAC-SHA256(seed, table‖column‖rowIndex) → uniform doubles/chars ──

    private static byte[] SubstreamBytes(int seed, string table, string column, int rowIndex)
    {
        var key = BitConverter.GetBytes(seed);
        var msg = Encoding.UTF8.GetBytes($"{table}{column}{rowIndex}");
        return HMACSHA256.HashData(key, msg);
    }

    private static double UniformDouble(int seed, string table, string column, int rowIndex)
    {
        var h = SubstreamBytes(seed, table, column, rowIndex);
        // 53-bit mantissa from first 8 bytes → [0, 1)
        var u = BitConverter.ToUInt64(h, 0) >> 11;
        return u / (double)(1UL << 53);
    }

    private static string RandomChars(int seed, string table, string column, int rowIndex, int length)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var h = SubstreamBytes(seed, table, column, rowIndex);
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
            sb.Append(Alphabet[h[i % h.Length] % Alphabet.Length]);
        return sb.ToString();
    }

    private static JsonElement Pick(OneOfRule o, int seed, string table, string column, int rowIndex)
        => o.Pick == OneOfPick.Cycle
            ? o.Values[rowIndex % o.Values.Count]
            : o.Values[(int)(UniformDouble(seed, table, column, rowIndex) * o.Values.Count)];

    private static object EvaluateRange(RangeRule r, AttributeMetadata attr, int seed, string table, int rowIndex)
    {
        var u = UniformDouble(seed, table, attr.LogicalName!, rowIndex);
        if (attr is DateTimeAttributeMetadata)
        {
            var min = RuleDate.Parse(r.Min.GetString()!);
            var max = RuleDate.Parse(r.Max.GetString()!);
            return DateTime.SpecifyKind(min + TimeSpan.FromTicks((long)((max - min).Ticks * u)), DateTimeKind.Utc);
        }
        var lo = r.Min.GetDecimal();
        var hi = r.Max.GetDecimal();
        return ConvertNumber(lo + (hi - lo) * (decimal)u, attr);
    }

    // ── S6: one conversion layer to exact SDK types — a type-rejection is not representable ──

    private static object? Convert(JsonElement value, AttributeMetadata attr) => attr switch
    {
        EnumAttributeMetadata => new OptionSetValue(value.GetInt32()),   // includes Status (statuscode) and Two-Options picklists
        BooleanAttributeMetadata => value.GetBoolean(),
        MoneyAttributeMetadata => new Money(value.GetDecimal()),
        DateTimeAttributeMetadata => RuleDate.Parse(value.GetString()!),
        IntegerAttributeMetadata => value.GetInt32(),
        BigIntAttributeMetadata => value.GetInt64(),
        DecimalAttributeMetadata => value.GetDecimal(),
        DoubleAttributeMetadata => value.GetDouble(),
        LookupAttributeMetadata lookup => ConvertLookup(value, lookup),
        _ => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
    };

    private static EntityReference ConvertLookup(JsonElement json, LookupAttributeMetadata attr)
        => LookupRuleValue.TryParse(json, attr, out var value, out var error)
            ? value!.ToReference()
            : throw new InvalidOperationException($"Invalid lookup value for '{attr.LogicalName}': {error}");

    private static object ConvertNumber(decimal n, AttributeMetadata attr) => attr switch
    {
        MoneyAttributeMetadata m => new Money(Math.Round(n, m.Precision ?? 2)),
        IntegerAttributeMetadata => (int)Math.Round(n),
        BigIntAttributeMetadata => (long)Math.Round(n),
        DoubleAttributeMetadata => (double)n,
        DecimalAttributeMetadata d => Math.Round(n, d.Precision ?? 2),
        _ => n,
    };
}

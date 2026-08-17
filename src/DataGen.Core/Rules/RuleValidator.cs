using System.Text.Json;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Rules;

/// <summary>Message severity for the editor MessageBar and pre-flight report (§3.3).</summary>
public enum RuleMessageSeverity
{
    /// <summary>Non-blocking notice.</summary>
    Warning,

    /// <summary>Blocking violation.</summary>
    Error
}

/// <summary>One validation message; <paramref name="Text"/> is user-facing copy, identical in both stages.</summary>
public sealed record RuleMessage(RuleMessageSeverity Severity, string Text);

/// <summary>
/// Validation outcome. <see cref="EffectiveRule"/> is the rule after clamping (range only);
/// all other ops pass through unchanged. Invalid ⇒ EffectiveRule is null.
/// </summary>
public sealed record RuleValidationResult(bool IsValid, FieldRule? EffectiveRule, IReadOnlyList<RuleMessage> Messages);

/// <summary>
/// Single validation code path used by BOTH the rule editor (design-time) and run pre-flight (§3.3).
/// Identical reason strings by construction — spec risk-table mitigation for metadata drift.
/// </summary>
public static class RuleValidator
{
    // D3: reserved TLDs never warn; anything else routable warns.
    private static readonly string[] ReservedTlds = [".test", ".invalid", ".example", ".localhost"];

    /// <summary>Validates one rule against live attribute metadata.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="attr">Live attribute metadata.</param>
    /// <param name="recordCount">Planned record count (sequence overflow, pattern worst-case seq width).</param>
    /// <param name="runId">Run id used only for {runId} worst-case length.</param>
    public static RuleValidationResult Validate(FieldRule rule, AttributeMetadata attr, int recordCount, string runId)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(attr);

        var eligibility = RuleEligibility.Classify(attr);
        if (!eligibility.IsSettable)
            return Invalid($"Column '{attr.LogicalName}' is not a rule target: {eligibility.Reason}.");

        return rule switch
        {
            ConstantRule c => ValidateConstant(c, attr),
            OneOfRule o => ValidateOneOf(o, attr),
            RangeRule r => ValidateRange(r, attr),
            PatternRule p => ValidatePattern(p, attr, recordCount, runId),
            SequenceRule s => ValidateSequence(s, attr, recordCount),
            NullRule n => ValidateNull(n, attr),
            _ => Invalid($"Unknown rule type '{rule.GetType().Name}'."),
        };
    }

    // ── per-op bodies ─────────────────────────────────────────────────────────

    private static RuleValidationResult ValidateConstant(ConstantRule c, AttributeMetadata attr) => attr switch
    {
        StringAttributeMetadata s when c.Value.ValueKind != JsonValueKind.String
            => Invalid($"'{attr.LogicalName}' expects a text value."),
        StringAttributeMetadata s when c.Value.GetString()!.Length > (s.MaxLength ?? int.MaxValue)
            => Invalid($"Value exceeds MaxLength {s.MaxLength} for '{attr.LogicalName}' — markers must never be silently truncated."),
        MemoAttributeMetadata m when c.Value.GetString()?.Length > (m.MaxLength ?? int.MaxValue)
            => Invalid($"Value exceeds MaxLength {m.MaxLength} for '{attr.LogicalName}'."),
        EnumAttributeMetadata e when !OptionExists(e, c.Value)
            => Invalid($"Option {c.Value} is not in '{attr.LogicalName}' — valid values are {OptionList(e)}."),
        BooleanAttributeMetadata when c.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            => Invalid($"'{attr.LogicalName}' expects true or false."),
        DateTimeAttributeMetadata when c.Value.ValueKind != JsonValueKind.String || !DateTime.TryParse(c.Value.GetString(), out _)
            => Invalid($"'{attr.LogicalName}' expects an ISO-8601 date."),
        _ when IsNumeric(attr) && c.Value.ValueKind != JsonValueKind.Number
            => Invalid($"'{attr.LogicalName}' expects a number."),
        _ when IsNumeric(attr) && OutOfBounds(attr, c.Value.GetDecimal())
            => Invalid($"Value {c.Value} is outside metadata bounds for '{attr.LogicalName}'."),
        _ => Valid(c),
    };

    private static RuleValidationResult ValidateOneOf(OneOfRule o, AttributeMetadata attr)
    {
        if (o.Values.Count < 2) return Invalid("one-of needs at least 2 values.");
        if (attr is EnumAttributeMetadata e)
        {
            var bad = o.Values.Where(v => !OptionExists(e, v)).ToList();
            if (bad.Count > 0)
                return Invalid($"Option {bad[0]} is not in '{attr.LogicalName}' — valid values are {OptionList(e)}.");
        }
        return Valid(o);
    }

    private static RuleValidationResult ValidateRange(RangeRule r, AttributeMetadata attr)
    {
        var (metaMin, metaMax) = MetadataBounds(attr);
        if (attr is DateTimeAttributeMetadata)
        {
            if (!DateTime.TryParse(r.Min.GetString(), out var dMin) || !DateTime.TryParse(r.Max.GetString(), out var dMax))
                return Invalid($"'{attr.LogicalName}' range needs ISO-8601 dates.");
            return dMin > dMax ? Invalid("Min must be ≤ Max.") : Valid(r);
        }

        var min = r.Min.GetDecimal();
        var max = r.Max.GetDecimal();
        if (min > max) return Invalid("Min must be ≤ Max.");

        var messages = new List<RuleMessage>();
        var (cMin, cMax) = (Math.Max(min, metaMin), Math.Min(max, metaMax));
        if (cMin != min || cMax != max)
            messages.Add(new(RuleMessageSeverity.Warning,
                $"Range for '{attr.LogicalName}' was clamped to metadata bounds: {min}–{max} → {cMin}–{cMax} (column bounds)."));

        var effective = (cMin != min || cMax != max)
            ? new RangeRule(ToElement(cMin), ToElement(cMax))
            : r;
        return new(true, effective, messages);
    }

    private static RuleValidationResult ValidatePattern(PatternRule p, AttributeMetadata attr, int recordCount, string runId)
    {
        PatternTemplate template;
        try { template = PatternTemplate.Parse(p.Template); }
        catch (FormatException ex) { return Invalid(ex.Message); }

        var maxLen = attr switch
        {
            StringAttributeMetadata s => s.MaxLength ?? int.MaxValue,
            MemoAttributeMetadata m => m.MaxLength ?? int.MaxValue,
            _ => -1,
        };
        if (maxLen < 0) return Invalid($"pattern applies to text columns only ('{attr.LogicalName}').");
        if (template.MaxExpandedLength(recordCount, runId.Length) > maxLen)
            return Invalid($"Worst-case expansion exceeds MaxLength {maxLen} for '{attr.LogicalName}' — markers must never be silently truncated.");

        var messages = new List<RuleMessage>();
        var literal = template.LiteralText();
        var at = literal.IndexOf('@');
        if (at >= 0)
        {
            var domain = literal[(at + 1)..].TrimEnd();
            if (domain.Length > 0 && !ReservedTlds.Any(t => domain.EndsWith(t, StringComparison.OrdinalIgnoreCase)))
                messages.Add(new(RuleMessageSeverity.Warning,
                    $"Domain '{domain}' is routable — generated addresses could deliver somewhere real. Reserved TLDs (.test / .invalid / .example) are recommended."));
        }
        return new(true, p, messages);
    }

    private static RuleValidationResult ValidateSequence(SequenceRule s, AttributeMetadata attr, int recordCount)
    {
        if (!IsNumeric(attr)) return Invalid($"sequence applies to numeric columns only ('{attr.LogicalName}').");
        var (metaMin, metaMax) = MetadataBounds(attr);
        var last = s.Start + (recordCount - 1) * s.Step;
        // Overflow is a design-time ERROR, never a clamp (§3.1 sequence row).
        if (s.Start < metaMin || last > metaMax || last < metaMin)
            return Invalid($"sequence {s.Start} + row·{s.Step} exceeds metadata bounds [{metaMin}, {metaMax}] within {recordCount} rows.");
        return Valid(s);
    }

    private static RuleValidationResult ValidateNull(NullRule n, AttributeMetadata attr)
        => attr.RequiredLevel?.Value is AttributeRequiredLevel.ApplicationRequired or AttributeRequiredLevel.SystemRequired
            ? Invalid($"'{attr.LogicalName}' is business-required — null rule not allowed.")
            : Valid(n);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RuleValidationResult Valid(FieldRule r) => new(true, r, []);
    private static RuleValidationResult Invalid(string text) => new(false, null, [new(RuleMessageSeverity.Error, text)]);

    private static bool IsNumeric(AttributeMetadata a) =>
        a is IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata
            or DoubleAttributeMetadata or MoneyAttributeMetadata;

    private static (decimal Min, decimal Max) MetadataBounds(AttributeMetadata a) => a switch
    {
        IntegerAttributeMetadata i => (i.MinValue ?? int.MinValue, i.MaxValue ?? int.MaxValue),
        BigIntAttributeMetadata b => (b.MinValue ?? long.MinValue, b.MaxValue ?? long.MaxValue),
        DecimalAttributeMetadata d => (d.MinValue ?? decimal.MinValue, d.MaxValue ?? decimal.MaxValue),
        DoubleAttributeMetadata f => (ToApplicationBound(f.MinValue, decimal.MinValue),
                                      ToApplicationBound(f.MaxValue, decimal.MaxValue)),
        MoneyAttributeMetadata m => (ToApplicationBound(m.MinValue, decimal.MinValue),
                                     ToApplicationBound(m.MaxValue, decimal.MaxValue)),
        _ => (decimal.MinValue, decimal.MaxValue),
    };

    /// <summary>Converts an optional SDK double bound to a decimal-representable application bound.</summary>
    private static decimal ToApplicationBound(double? sdkBound, decimal fallback)
    {
        if (sdkBound is not { } v || double.IsNaN(v) || double.IsInfinity(v))
            return fallback;
        if (v <= (double)decimal.MinValue) return decimal.MinValue;
        if (v >= (double)decimal.MaxValue) return decimal.MaxValue;
        return (decimal)v;
    }

    private static bool OutOfBounds(AttributeMetadata a, decimal v)
    { var (min, max) = MetadataBounds(a); return v < min || v > max; }

    private static bool OptionExists(EnumAttributeMetadata e, JsonElement v) =>
        v.ValueKind == JsonValueKind.Number
        && e.OptionSet?.Options.Any(o => o.Value == v.GetInt32()) == true;

    private static string OptionList(EnumAttributeMetadata e) =>
        string.Join(", ", e.OptionSet?.Options.Select(o => o.Value) ?? []);

    private static JsonElement ToElement(decimal d) =>
        JsonDocument.Parse(d.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement;
}

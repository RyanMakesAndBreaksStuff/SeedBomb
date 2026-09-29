using Microsoft.Xrm.Sdk.Metadata;
using System.Globalization;
using System.Text.Json;

namespace SeedBomb.Core.Rules;

/// <summary>Message severity for the editor MessageBar and pre-flight report (§3.3).</summary>
public enum RuleMessageSeverity
{
    /// <summary>Non-blocking notice.</summary>
    Warning,

    /// <summary>Blocking violation.</summary>
    Error
}

/// <summary>Stable reason code. WPF maps this; it never parses <see cref="RuleMessage.Text"/>.</summary>
public enum RuleMessageCode
{
    /// <summary>Unspecified or legacy message.</summary>
    General,

    /// <summary>API/endpoint pair is not in the catalog.</summary>
    UnknownEndpoint,

    /// <summary>Argument key is not on the descriptor contract.</summary>
    UnknownArgument,

    /// <summary>Required argument is absent.</summary>
    MissingArgument,

    /// <summary>JSON value kind or non-finite number is wrong for the argument.</summary>
    BadValueKind,

    /// <summary>Number is outside a representable range.</summary>
    Overflow,

    /// <summary>Normalized domain contains no representable value.</summary>
    EmptyDomain,

    /// <summary>Known-input length or preparation budget is exceeded.</summary>
    LengthBudget,

    /// <summary>Date is malformed, reversed, or outside SDK bounds.</summary>
    DateRange,

    /// <summary>Endpoint cannot produce the column's Dataverse kind or domain.</summary>
    Incompatible,

    /// <summary>Descriptor is risky; saveable, blocked at run without opt-in.</summary>
    RiskWarning,

    /// <summary>Engine version is missing or unsupported.</summary>
    EngineVersion,

    /// <summary>Bogus rule was validated without <see cref="RuleValidationContext"/>.</summary>
    ContextRequired,
}

/// <summary>Editor input that produced the message.</summary>
public enum RuleInputTarget
{
    /// <summary>The rule as a whole.</summary>
    Rule,

    /// <summary>Catalog API picker.</summary>
    Api,

    /// <summary>Catalog endpoint picker.</summary>
    Endpoint,

    /// <summary>Numeric minimum.</summary>
    Minimum,

    /// <summary>Numeric maximum.</summary>
    Maximum,

    /// <summary>Length argument.</summary>
    Length,

    /// <summary>Date minimum.</summary>
    MinimumDate,

    /// <summary>Date maximum.</summary>
    MaximumDate,
}

/// <summary>One validation message; <paramref name="Text"/> is user-facing copy, identical in both stages.</summary>
/// <param name="Severity">Blocking vs notice.</param>
/// <param name="Text">User-facing copy.</param>
/// <param name="Code">Stable reason code.</param>
/// <param name="Target">Editor input that produced the message.</param>
public sealed record RuleMessage(
    RuleMessageSeverity Severity,
    string Text,
    RuleMessageCode Code = RuleMessageCode.General,
    RuleInputTarget Target = RuleInputTarget.Rule);

/// <summary>
/// Validation outcome. <see cref="EffectiveRule"/> is the rule after clamping (range)
/// or Bogus normalization; all other ops pass through unchanged. Invalid ⇒ EffectiveRule is null.
/// </summary>
public sealed record RuleValidationResult(bool IsValid, FieldRule? EffectiveRule, IReadOnlyList<RuleMessage> Messages)
{
    /// <summary>Blocking result with one or more messages and no effective rule.</summary>
    public static RuleValidationResult Error(RuleMessage message) => new(false, null, [message]);
}

/// <summary>Static validation inputs. Carries no seed — output-dependent checks wait for run preparation.</summary>
/// <param name="Table">Canonical table logical name, when known.</param>
/// <param name="RecordCount">Planned row count for this table.</param>
/// <param name="RunId">Run id used for pattern worst-case width.</param>
public readonly record struct RuleValidationContext(string Table, int RecordCount, string RunId);

/// <summary>
/// Single validation code path used by BOTH the rule editor (design-time) and run pre-flight (§3.3).
/// Identical reason strings by construction — spec risk-table mitigation for metadata drift.
/// </summary>
public static class RuleValidator
{
    // D3: reserved TLDs never warn; anything else routable warns.
    private static readonly string[] ReservedTlds = [".test", ".invalid", ".example", ".localhost"];

    private const int CurrentEngineVersion = 1;
    private const int MaxCellUtf16Units = 65_536;
    private const long MaxRulePreparationBytes = 64L * 1024 * 1024;
    private const long MaxTotalPreparationBytes = 256L * 1024 * 1024;

    /// <summary>Validates one rule against live attribute metadata.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="attr">Live attribute metadata.</param>
    /// <param name="context">Table, planned count, and run id. Required for Bogus rules.</param>
    public static RuleValidationResult Validate(FieldRule rule, AttributeMetadata attr, RuleValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(attr);

        var eligibility = RuleEligibility.Classify(attr);
        if (!eligibility.IsSettable)
            return Invalid($"Column '{attr.LogicalName}' is not a rule target: {eligibility.Reason}.");

        if (attr is LookupAttributeMetadata
            && rule is not (ConstantRule or OneOfRule or NullRule or LookupRandomRule))
            return Invalid($"'{attr.LogicalName}' lookup rules must be constant, one-of, null, or lookupRandom.");

        return rule switch
        {
            BogusRule b => ValidateBogus(b, attr, context),
            ConstantRule c => ValidateConstant(c, attr),
            OneOfRule o => ValidateOneOf(o, attr),
            RangeRule r => ValidateRange(r, attr),
            PatternRule p => ValidatePattern(p, attr, context.RecordCount, context.RunId),
            SequenceRule s => ValidateSequence(s, attr, context.RecordCount),
            NullRule n => ValidateNull(n, attr),
            LookupRandomRule => attr is LookupAttributeMetadata
                ? Valid(rule)
                : Invalid($"lookupRandom applies to lookup columns only ('{attr.LogicalName}')."),
            _ => Invalid($"Unknown rule type '{rule.GetType().Name}'."),
        };
    }

    /// <summary>Adapter for existing non-Bogus callers. Migrated away by Task 9.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="attr">Live attribute metadata.</param>
    /// <param name="recordCount">Planned record count (sequence overflow, pattern worst-case seq width).</param>
    /// <param name="runId">Run id used only for {runId} worst-case length.</param>
    public static RuleValidationResult Validate(FieldRule rule, AttributeMetadata attr, int recordCount, string runId) =>
        rule is BogusRule
            ? RuleValidationResult.Error(new RuleMessage(RuleMessageSeverity.Error,
                  "Bogus rules require a validation context.", RuleMessageCode.ContextRequired))
            : Validate(rule, attr, new RuleValidationContext(Table: string.Empty, recordCount, runId));

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
        DateTimeAttributeMetadata when c.Value.ValueKind != JsonValueKind.String || !RuleDate.TryParse(c.Value.GetString(), out _)
            => Invalid($"'{attr.LogicalName}' expects an ISO-8601 date."),
        _ when IsNumeric(attr) && c.Value.ValueKind != JsonValueKind.Number
            => Invalid($"'{attr.LogicalName}' expects a number."),
        _ when IsNumeric(attr) && OutOfBounds(attr, c.Value.GetDecimal())
            => Invalid($"Value {c.Value} is outside metadata bounds for '{attr.LogicalName}'."),
        LookupAttributeMetadata lookup => ValidateLookupConstant(c, lookup),
        _ => Valid(c),
    };

    private static RuleValidationResult ValidateLookupConstant(
        ConstantRule rule, LookupAttributeMetadata attr)
        => LookupRuleValue.TryParse(rule.Value, attr, out var value, out var error)
            ? Valid(new ConstantRule(value!.ToJson()))
            : Invalid($"'{attr.LogicalName}': {error}");

    private static RuleValidationResult ValidateOneOf(OneOfRule o, AttributeMetadata attr)
    {
        if (o.Values is null || o.Values.Count < 2)
            return Invalid(attr is LookupAttributeMetadata
                ? "Choose at least two records for one-of, or use constant for one record."
                : "one-of needs at least 2 values.");
        if (!Enum.IsDefined(o.Pick)) return Invalid("Unknown one-of pick mode.");
        if (attr is LookupAttributeMetadata lookup)
        {
            var normalized = new List<JsonElement>(o.Values.Count);
            for (var index = 0; index < o.Values.Count; index++)
            {
                if (!LookupRuleValue.TryParse(o.Values[index], lookup, out var value, out var error))
                    return Invalid($"'{attr.LogicalName}', value {index + 1}: {error}");
                normalized.Add(value!.ToJson());
            }
            return Valid(o with { Values = normalized.AsReadOnly() });
        }
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
            if (!RuleDate.TryParse(r.Min.GetString(), out var dMin) || !RuleDate.TryParse(r.Max.GetString(), out var dMax))
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

    // ── Bogus phases 1–3 ──────────────────────────────────────────────────────

    private static RuleValidationResult ValidateBogus(BogusRule rule, AttributeMetadata attr, RuleValidationContext context)
    {
        if (rule.EngineVersion != CurrentEngineVersion)
        {
            return Fail(RuleMessageCode.EngineVersion, RuleInputTarget.Rule,
                $"Engine version {rule.EngineVersion} is not supported.");
        }

        if (!BogusCatalog.TryGet(new BogusEndpointId(rule.Api, rule.Endpoint), out var descriptor))
        {
            return Fail(RuleMessageCode.UnknownEndpoint, RuleInputTarget.Endpoint,
                $"Unknown Bogus endpoint '{rule.Api}.{rule.Endpoint}'.");
        }

        if (!TryMapValueKind(attr, out var target) || !BogusCatalog.Fits(descriptor, target))
        {
            return Fail(RuleMessageCode.Incompatible, RuleInputTarget.Rule,
                $"'{descriptor.Id}' cannot generate values for '{attr.LogicalName}'.");
        }

        var keys = new HashSet<string>(rule.Args.Keys, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!IsAllowedKey(descriptor.Arguments, key))
            {
                return Fail(RuleMessageCode.UnknownArgument, RuleInputTarget.Rule,
                    $"Unknown argument '{key}' for '{descriptor.Id}'.");
            }
        }

        return descriptor.Arguments switch
        {
            NumericRangeContract numeric => NormalizeNumeric(rule, attr, descriptor, numeric, context),
            LengthContract length => NormalizeLength(rule, attr, descriptor, length, context),
            DateRangeContract => NormalizeDates(rule, attr, descriptor, context),
            _ => FinishNone(rule, attr, descriptor, context),
        };
    }

    private static bool IsAllowedKey(BogusArgumentContract contract, string key) => contract switch
    {
        NumericRangeContract { AcceptsAuthoredMinMax: true } => key is "min" or "max",
        LengthContract => key is "length",
        DateRangeContract => key is "min" or "max",
        _ => false,
    };

    private static RuleValidationResult NormalizeNumeric(
        BogusRule rule, AttributeMetadata attr, BogusEndpointDescriptor descriptor,
        NumericRangeContract contract, RuleValidationContext context)
    {
        _ = context;
        if (!TryReadOptionalNumber(rule.Args, "min", RuleInputTarget.Minimum, out var authoredMin, out var minError))
            return minError!;
        if (!TryReadOptionalNumber(rule.Args, "max", RuleInputTarget.Maximum, out var authoredMax, out var maxError))
            return maxError!;

        if (!contract.AcceptsAuthoredMinMax && (authoredMin is not null || authoredMax is not null))
        {
            return Fail(RuleMessageCode.UnknownArgument, RuleInputTarget.Rule,
                $"'{descriptor.Id}' does not accept authored min/max.");
        }

        var min = authoredMin ?? contract.DefaultMin;
        var max = authoredMax ?? contract.DefaultMax;
        if (min > max)
        {
            return Fail(RuleMessageCode.EmptyDomain, RuleInputTarget.Maximum,
                $"Minimum {min} is greater than maximum {max}.");
        }

        min = decimal.Max(min, contract.DefaultMin);
        max = decimal.Min(max, contract.DefaultMax);
        if (min > max)
        {
            return Fail(RuleMessageCode.EmptyDomain, RuleInputTarget.Maximum,
                $"Authored range is outside the native domain of '{descriptor.Id}'.");
        }

        if (IsFixedDomain(contract, descriptor) && !MetadataContains(attr, contract.DefaultMin, contract.DefaultMax))
        {
            return Fail(RuleMessageCode.Incompatible, RuleInputTarget.Rule,
                $"'{attr.LogicalName}' cannot hold the full '{descriptor.Id}' domain [{contract.DefaultMin}, {contract.DefaultMax}].");
        }

        var (metaMin, metaMax) = MetadataBounds(attr);
        var beforeMeta = (min, max);
        min = decimal.Max(min, metaMin);
        max = decimal.Min(max, metaMax);
        if (min > max)
        {
            return Fail(RuleMessageCode.EmptyDomain, RuleInputTarget.Maximum,
                $"No overlap between '{descriptor.Id}' and '{attr.LogicalName}' bounds.");
        }

        var messages = new List<RuleMessage>();
        if (contract.AcceptsAuthoredMinMax && (beforeMeta.min != min || beforeMeta.max != max
                                              || authoredMin != min || authoredMax != max)
            && (authoredMin is not null || authoredMax is not null)
            && (authoredMin != min || authoredMax != max))
        {
            messages.Add(new(RuleMessageSeverity.Warning,
                $"Range for '{descriptor.Id}' on '{attr.LogicalName}' was clamped to [{min}, {max}].",
                RuleMessageCode.General, RuleInputTarget.Rule));
        }

        if (IsIntegral(descriptor, contract))
        {
            min = decimal.Ceiling(min);
            max = decimal.Floor(max);
            if (min > max)
            {
                return Fail(RuleMessageCode.EmptyDomain, RuleInputTarget.Maximum,
                    $"No integral value remains in [{beforeMeta.min}, {beforeMeta.max}] for '{descriptor.Id}'.");
            }

            if (descriptor.Id.Endpoint == "even")
            {
                min = AlignEven(min, up: true);
                max = AlignEven(max, up: false);
            }
            else if (descriptor.Id.Endpoint == "odd")
            {
                min = AlignOdd(min, up: true);
                max = AlignOdd(max, up: false);
            }

            if (min > max)
            {
                return Fail(RuleMessageCode.EmptyDomain, RuleInputTarget.Maximum,
                    $"No value of the required parity remains for '{descriptor.Id}'.");
            }
        }

        if (!FitsNative(min, max, descriptor, contract))
        {
            return Fail(RuleMessageCode.Overflow, RuleInputTarget.Rule,
                $"Normalized bounds [{min}, {max}] are not representable for '{descriptor.Id}'.");
        }

        if (min < metaMin || max > metaMax)
        {
            return Fail(RuleMessageCode.EmptyDomain, RuleInputTarget.Maximum,
                $"Quantized bounds [{min}, {max}] fall outside '{attr.LogicalName}' metadata.");
        }

        IReadOnlyDictionary<string, JsonElement>? args = null;
        if (contract.AcceptsAuthoredMinMax)
            args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["min"] = JsonNumber(min),
                ["max"] = JsonNumber(max),
            };

        return Succeed(new BogusRule(rule.Api, rule.Endpoint, rule.EngineVersion, args), descriptor, messages);
    }

    private static RuleValidationResult NormalizeLength(
        BogusRule rule, AttributeMetadata attr, BogusEndpointDescriptor descriptor,
        LengthContract contract, RuleValidationContext context)
    {
        int length;
        if (rule.Args.TryGetValue("length", out var raw))
        {
            if (!TryReadDecimal(raw, RuleInputTarget.Length, out var parsed, out var error))
                return error!;
            if (parsed != decimal.Truncate(parsed))
            {
                return Fail(RuleMessageCode.BadValueKind, RuleInputTarget.Length,
                    "length must be an integer greater than zero.");
            }

            if (parsed <= 0)
            {
                return Fail(RuleMessageCode.LengthBudget, RuleInputTarget.Length,
                    "length must be an integer greater than zero.");
            }

            if (parsed > int.MaxValue)
            {
                return Fail(RuleMessageCode.Overflow, RuleInputTarget.Length,
                    "length exceeds Int32.");
            }

            length = (int)parsed;
        }
        else
        {
            length = contract.DefaultLength;
        }

        var lengthError = CheckKnownLength(length, descriptor.Output.Multiplier, attr, context, RuleInputTarget.Length);
        if (lengthError is not null)
            return lengthError;

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["length"] = JsonNumber(length),
        };
        return Succeed(new BogusRule(rule.Api, rule.Endpoint, rule.EngineVersion, args), descriptor, []);
    }

    private static RuleValidationResult NormalizeDates(
        BogusRule rule, AttributeMetadata attr, BogusEndpointDescriptor descriptor,
        RuleValidationContext context)
    {
        _ = (attr, context);
        if (!rule.Args.TryGetValue("min", out var minEl))
        {
            return Fail(RuleMessageCode.MissingArgument, RuleInputTarget.MinimumDate,
                "DATE.between requires min.");
        }

        if (!rule.Args.TryGetValue("max", out var maxEl))
        {
            return Fail(RuleMessageCode.MissingArgument, RuleInputTarget.MaximumDate,
                "DATE.between requires max.");
        }

        if (!TryReadDate(minEl, RuleInputTarget.MinimumDate, out var min, out var minError))
            return minError!;
        if (!TryReadDate(maxEl, RuleInputTarget.MaximumDate, out var max, out var maxError))
            return maxError!;

        if (min > max)
        {
            return Fail(RuleMessageCode.DateRange, RuleInputTarget.MaximumDate,
                "Minimum date must be ≤ maximum date.");
        }

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["min"] = JsonSerializer.SerializeToElement(min.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["max"] = JsonSerializer.SerializeToElement(max.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        };
        return Succeed(new BogusRule(rule.Api, rule.Endpoint, rule.EngineVersion, args), descriptor, []);
    }

    private static RuleValidationResult FinishNone(
        BogusRule rule, AttributeMetadata attr, BogusEndpointDescriptor descriptor,
        RuleValidationContext context)
    {
        if (descriptor.Output.Policy == BogusLengthPolicy.Fixed && descriptor.Output.Length is { } fixedLen)
        {
            var lengthError = CheckKnownLength(fixedLen, 1, attr, context, RuleInputTarget.Rule);
            if (lengthError is not null)
                return lengthError;
        }

        return Succeed(new BogusRule(rule.Api, rule.Endpoint, rule.EngineVersion), descriptor, []);
    }

    private static RuleValidationResult? CheckKnownLength(
        long length, int multiplier, AttributeMetadata attr, RuleValidationContext context, RuleInputTarget target)
    {
        long outputChars;
        try
        {
            outputChars = checked(length * multiplier);
        }
        catch (OverflowException)
        {
            return Fail(RuleMessageCode.LengthBudget, target, "length exceeds the per-cell UTF-16 budget.");
        }

        if (outputChars > MaxCellUtf16Units)
        {
            return Fail(RuleMessageCode.LengthBudget, target,
                $"Generated text would be {outputChars} UTF-16 units; the per-cell maximum is {MaxCellUtf16Units}.");
        }

        var columnMax = attr switch
        {
            StringAttributeMetadata s => s.MaxLength,
            MemoAttributeMetadata m => m.MaxLength,
            _ => null,
        };
        if (columnMax is { } maxLen && outputChars > maxLen)
        {
            return Fail(RuleMessageCode.LengthBudget, target,
                $"Generated length {outputChars} exceeds MaxLength {maxLen} for '{attr.LogicalName}'.");
        }

        if (context.RecordCount < 0)
        {
            return Fail(RuleMessageCode.LengthBudget, RuleInputTarget.Rule, "Record count must be nonnegative.");
        }

        long accounting;
        try
        {
            accounting = checked(context.RecordCount * (long)IntPtr.Size + outputChars * sizeof(char));
        }
        catch (OverflowException)
        {
            return Fail(RuleMessageCode.LengthBudget, target, "Preparation accounting overflowed.");
        }

        if (accounting > MaxRulePreparationBytes || accounting > MaxTotalPreparationBytes)
        {
            return Fail(RuleMessageCode.LengthBudget, target, "Known-input preparation budget exceeded.");
        }

        return null;
    }

    private static bool TryReadOptionalNumber(
        IReadOnlyDictionary<string, JsonElement> args, string key, RuleInputTarget target,
        out decimal? value, out RuleValidationResult? error)
    {
        value = null;
        error = null;
        if (!args.TryGetValue(key, out var raw))
            return true;
        if (!TryReadDecimal(raw, target, out var parsed, out error))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryReadDecimal(
        JsonElement element, RuleInputTarget target, out decimal value, out RuleValidationResult? error)
    {
        value = 0;
        error = null;
        if (element.ValueKind != JsonValueKind.Number)
        {
            error = Fail(RuleMessageCode.BadValueKind, target, "Expected a finite JSON number.");
            return false;
        }

        var raw = element.GetRawText();
        if (raw.Contains("NaN", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Infinity", StringComparison.OrdinalIgnoreCase))
        {
            error = Fail(RuleMessageCode.BadValueKind, target, "Number must be finite.");
            return false;
        }

        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = Fail(RuleMessageCode.Overflow, target, "Number is outside the decimal range.");
            return false;
        }

        return true;
    }

    private static bool TryReadDate(
        JsonElement element, RuleInputTarget target, out DateOnly value, out RuleValidationResult? error)
    {
        value = default;
        error = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            error = Fail(RuleMessageCode.BadValueKind, target, "Date must be a yyyy-MM-dd string.");
            return false;
        }

        var text = element.GetString();
        if (text is null
            || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            error = Fail(RuleMessageCode.DateRange, target, "Date must be exactly yyyy-MM-dd.");
            return false;
        }

        var sdkMin = DateOnly.FromDateTime(DateTimeAttributeMetadata.MinSupportedValue);
        var sdkMax = DateOnly.FromDateTime(DateTimeAttributeMetadata.MaxSupportedValue);
        if (value < sdkMin || value > sdkMax)
        {
            error = Fail(RuleMessageCode.DateRange, target,
                $"Date must be between {sdkMin:yyyy-MM-dd} and {sdkMax:yyyy-MM-dd}.");
            return false;
        }

        return true;
    }

    private static bool TryMapValueKind(AttributeMetadata attr, out DataverseValueKind kind)
    {
        switch (attr)
        {
            case StringAttributeMetadata:
                kind = DataverseValueKind.String;
                return true;
            case MemoAttributeMetadata:
                kind = DataverseValueKind.Memo;
                return true;
            case BooleanAttributeMetadata:
                kind = DataverseValueKind.Boolean;
                return true;
            case IntegerAttributeMetadata:
                kind = DataverseValueKind.Integer;
                return true;
            case BigIntAttributeMetadata:
                kind = DataverseValueKind.BigInt;
                return true;
            case DecimalAttributeMetadata:
                kind = DataverseValueKind.Decimal;
                return true;
            case DoubleAttributeMetadata:
                kind = DataverseValueKind.Double;
                return true;
            case MoneyAttributeMetadata:
                kind = DataverseValueKind.Money;
                return true;
            case DateTimeAttributeMetadata:
                kind = DataverseValueKind.DateTime;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool IsIntegral(BogusEndpointDescriptor descriptor, NumericRangeContract contract)
    {
        if (descriptor.RawKind == BogusRawKind.Int32)
            return true;
        return descriptor.RawKind == BogusRawKind.Decimal
               && contract.AcceptsAuthoredMinMax
               && IsWhole(contract.DefaultMin)
               && IsWhole(contract.DefaultMax)
               && !(contract.DefaultMin == 0 && contract.DefaultMax == 1);
    }

    private static bool IsFixedDomain(NumericRangeContract contract, BogusEndpointDescriptor descriptor) =>
        !contract.AcceptsAuthoredMinMax && descriptor.RawKind == BogusRawKind.Int32
                                        && contract.DefaultMin == 1 && contract.DefaultMax == 65535;

    private static bool MetadataContains(AttributeMetadata attr, decimal domainMin, decimal domainMax)
    {
        var (metaMin, metaMax) = MetadataBounds(attr);
        return metaMin <= domainMin && metaMax >= domainMax;
    }

    private static bool FitsNative(
        decimal min, decimal max, BogusEndpointDescriptor descriptor, NumericRangeContract contract)
    {
        if (descriptor.RawKind == BogusRawKind.Int32)
            return min >= int.MinValue && max <= int.MaxValue;
        if (descriptor.RawKind == BogusRawKind.Double)
            return IsFiniteDouble(min) && IsFiniteDouble(max);
        if (IsIntegral(descriptor, contract))
        {
            if (contract.DefaultMin == 0 && contract.DefaultMax == uint.MaxValue)
                return min >= 0 && max <= uint.MaxValue;
            if (contract.DefaultMin == 0 && contract.DefaultMax == ulong.MaxValue)
                return min >= 0 && max <= ulong.MaxValue;
            return min >= long.MinValue && max <= long.MaxValue;
        }

        return true;
    }

    private static bool IsFiniteDouble(decimal value)
    {
        var d = (double)value;
        return !double.IsNaN(d) && !double.IsInfinity(d);
    }

    private static bool IsWhole(decimal value) => value == decimal.Truncate(value);

    private static decimal AlignEven(decimal value, bool up)
    {
        if (decimal.Remainder(value, 2) == 0)
            return value;
        return up ? value + 1 : value - 1;
    }

    private static decimal AlignOdd(decimal value, bool up)
    {
        if (decimal.Remainder(value, 2) != 0)
            return value;
        return up ? value + 1 : value - 1;
    }

    private static RuleValidationResult Succeed(
        BogusRule effective, BogusEndpointDescriptor descriptor, List<RuleMessage> messages)
    {
        if (descriptor.Risk != BogusRiskClass.None)
        {
            messages.Add(new(RuleMessageSeverity.Warning,
                $"'{descriptor.Id}' produces {descriptor.Risk} values and requires run-time acknowledgement.",
                RuleMessageCode.RiskWarning, RuleInputTarget.Rule));
        }

        return new(true, effective, messages);
    }

    private static RuleValidationResult Fail(RuleMessageCode code, RuleInputTarget target, string text) =>
        RuleValidationResult.Error(new RuleMessage(RuleMessageSeverity.Error, text, code, target));

    private static JsonElement JsonNumber(decimal value) =>
        JsonSerializer.SerializeToElement(value);

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
        JsonDocument.Parse(d.ToString(CultureInfo.InvariantCulture)).RootElement;
}

using System.Globalization;
using System.Text.Json;
using DataGen.Core.Generators;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Rules;

/// <summary>Inputs shared by preview, review, preparation, and production evaluation.</summary>
/// <param name="Table">Canonical table logical name.</param>
/// <param name="Seed">Run seed. Framed per cell; never a substituted constant.</param>
/// <param name="Locale">Bogus locale. v1 accepts only <c>en</c>.</param>
/// <param name="RunId">Run id. Participates in validation, not in seed framing.</param>
/// <param name="RecordCount">Planned row count. Participates in validation, not in seed framing.</param>
public readonly record struct RuleEvaluationContext(
    string Table,
    int Seed,
    string Locale,
    string RunId,
    int RecordCount);

/// <summary>
/// Compiled Bogus rule: resolved descriptor, normalized arguments, and target date mode.
/// No generated values.
/// </summary>
public sealed class PreparedBogusRule
{
    internal PreparedBogusRule(
        BogusEndpointDescriptor descriptor,
        NormalizedBogusArgs args,
        string canonicalTable,
        string canonicalColumn,
        int engineVersion,
        int recordCount,
        bool useDateOnly,
        int scale)
    {
        Descriptor = descriptor;
        Args = args;
        CanonicalTable = canonicalTable;
        CanonicalColumn = canonicalColumn;
        EngineVersion = engineVersion;
        RecordCount = recordCount;
        UseDateOnly = useDateOnly;
        Scale = scale;
    }

    internal BogusEndpointDescriptor Descriptor { get; }

    internal NormalizedBogusArgs Args { get; }

    /// <summary>Table logical name captured at compile time.</summary>
    public string CanonicalTable { get; }

    /// <summary>Column logical name from attribute metadata.</summary>
    public string CanonicalColumn { get; }

    /// <summary>Evaluator engine version this rule was compiled for.</summary>
    public int EngineVersion { get; }

    /// <summary>Record count captured at compile time. Does not change row values.</summary>
    public int RecordCount { get; }

    /// <summary>True when the target DateTime column uses DateOnly behavior.</summary>
    public bool UseDateOnly { get; }

    /// <summary>Decimal/Money scale carried into <c>FINANCE.amount</c>.</summary>
    public int Scale { get; }

    /// <summary>Confirms the evaluation context can consume this prepared rule.</summary>
    /// <param name="context">The evaluation context for this cell.</param>
    public void EnsureMatches(RuleEvaluationContext context)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(context.RecordCount);
        if (string.IsNullOrWhiteSpace(context.Table))
            throw new InvalidOperationException("Table is required.");
        if (EngineVersion != 1)
            throw new InvalidOperationException($"Engine version {EngineVersion} is not supported.");
        if (!string.Equals(context.Locale, DeterministicFaker.DefaultLocale, StringComparison.Ordinal))
            throw new InvalidOperationException("v1 accepts only locale 'en'.");
    }

    internal BogusInvocationContext CreateInvocationContext(Bogus.Faker faker) =>
        new(faker, Args, DeterministicFaker.ReferenceDate, UseDateOnly, Scale);
}

/// <summary>Compiles a normalized Bogus rule into a prepared, non-generating form.</summary>
public static class BogusRulePreparer
{
    /// <summary>
    /// Resolves the catalog descriptor, normalized arguments, and target date mode.
    /// Does not generate values.
    /// </summary>
    /// <param name="effectiveRule">A Bogus rule, typically the validator's <c>EffectiveRule</c>.</param>
    /// <param name="attr">Live attribute metadata.</param>
    /// <param name="context">Evaluation context for this table.</param>
    public static PreparedBogusRule CompileRule(
        BogusRule effectiveRule, AttributeMetadata attr, RuleEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(effectiveRule);
        ArgumentNullException.ThrowIfNull(attr);
        if (string.IsNullOrWhiteSpace(context.Table))
            throw new InvalidOperationException("Table is required.");
        ArgumentOutOfRangeException.ThrowIfNegative(context.RecordCount);
        if (string.IsNullOrWhiteSpace(attr.LogicalName))
            throw new InvalidOperationException("Column logical name is required.");

        var validation = RuleValidator.Validate(
            effectiveRule, attr, new RuleValidationContext(context.Table, context.RecordCount, context.RunId));
        if (!validation.IsValid || validation.EffectiveRule is not BogusRule normalized)
        {
            var text = validation.Messages.Count > 0 ? validation.Messages[0].Text : "Invalid Bogus rule.";
            throw new InvalidOperationException(text);
        }

        if (!BogusCatalog.TryGet(new BogusEndpointId(normalized.Api, normalized.Endpoint), out var descriptor))
            throw new InvalidOperationException($"Unknown Bogus endpoint '{normalized.Api}.{normalized.Endpoint}'.");

        var useDateOnly = attr is DateTimeAttributeMetadata dt
            && string.Equals(dt.DateTimeBehavior?.Value, "DateOnly", StringComparison.Ordinal);
        var scale = attr switch
        {
            DecimalAttributeMetadata d => d.Precision ?? 2,
            MoneyAttributeMetadata m => m.Precision ?? 2,
            _ => 2,
        };

        return new PreparedBogusRule(
            descriptor,
            BuildArgs(normalized, descriptor, attr, scale),
            context.Table,
            attr.LogicalName,
            normalized.EngineVersion,
            context.RecordCount,
            useDateOnly,
            scale);
    }

    private static NormalizedBogusArgs BuildArgs(
        BogusRule rule, BogusEndpointDescriptor descriptor, AttributeMetadata attr, int scale)
    {
        switch (descriptor.Arguments)
        {
            case NumericRangeContract contract:
            {
                decimal min;
                decimal max;
                if (rule.Args.TryGetValue("min", out var minEl)
                    && rule.Args.TryGetValue("max", out var maxEl)
                    && minEl.TryGetDecimal(out min)
                    && maxEl.TryGetDecimal(out max))
                {
                    return new NumericRangeArgs(min, max, scale);
                }

                min = contract.DefaultMin;
                max = contract.DefaultMax;
                var (metaMin, metaMax) = MetadataBounds(attr);
                min = decimal.Max(min, metaMin);
                max = decimal.Min(max, metaMax);
                return new NumericRangeArgs(min, max, scale);
            }
            case LengthContract contract:
            {
                var length = rule.Args.TryGetValue("length", out var lenEl) && lenEl.TryGetInt32(out var parsed)
                    ? parsed
                    : contract.DefaultLength;
                return new LengthArgs(length);
            }
            case DateRangeContract:
            {
                var minText = rule.Args["min"].GetString()
                    ?? throw new InvalidOperationException("DATE.between requires min.");
                var maxText = rule.Args["max"].GetString()
                    ?? throw new InvalidOperationException("DATE.between requires max.");
                var min = DateOnly.ParseExact(minText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var max = DateOnly.ParseExact(maxText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return new DateRangeArgs(min, max);
            }
            default:
                return NormalizedBogusArgs.None;
        }
    }

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

    private static decimal ToApplicationBound(double? sdkBound, decimal fallback)
    {
        if (sdkBound is not { } v || double.IsNaN(v) || double.IsInfinity(v))
            return fallback;
        if (v <= (double)decimal.MinValue) return decimal.MinValue;
        if (v >= (double)decimal.MaxValue) return decimal.MaxValue;
        return (decimal)v;
    }
}

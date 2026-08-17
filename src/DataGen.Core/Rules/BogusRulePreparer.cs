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

    private const long MaxRulePreparationBytes = 64L * 1024 * 1024;
    private const long MaxTotalPreparationBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Prepares every table in one atomic operation. Pass 1 compiles and validates all requests and
    /// scans all risk; no dynamic value is generated until it succeeds. Pass 2 fills bounded caches.
    /// </summary>
    /// <param name="requests">Every Bogus column in the run, all tables together.</param>
    /// <param name="allowRiskyValues">Run-scoped opt-in. Never read from profile state.</param>
    /// <param name="cancellationToken">Observed during row preparation.</param>
    public static async Task<BogusRunPreparationResult> PrepareRun(
        IReadOnlyList<BogusPreparationRequest> requests,
        bool allowRiskyValues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();

        var messages = new List<RuleMessage>();
        var compiled = new List<(BogusPreparationRequest Request, PreparedBogusRule Rule, bool Preflight)>();
        var blocked = false;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(request.EffectiveRule);
            ArgumentNullException.ThrowIfNull(request.Attribute);

            var validation = RuleValidator.Validate(
                request.EffectiveRule,
                request.Attribute,
                new RuleValidationContext(request.TableLogicalName, request.Context.RecordCount, request.Context.RunId));

            if (!validation.IsValid)
            {
                blocked = true;
                messages.AddRange(validation.Messages);
                continue;
            }

            PreparedBogusRule rule;
            try
            {
                var effective = validation.EffectiveRule as BogusRule ?? request.EffectiveRule;
                var compileContext = request.Context with { Table = request.TableLogicalName };
                rule = CompileRule(effective, request.Attribute, compileContext);
            }
            catch (InvalidOperationException ex)
            {
                blocked = true;
                messages.Add(new RuleMessage(RuleMessageSeverity.Error, ex.Message, RuleMessageCode.General));
                continue;
            }

            if (rule.Descriptor.Risk != BogusRiskClass.None)
            {
                messages.Add(new RuleMessage(
                    allowRiskyValues ? RuleMessageSeverity.Warning : RuleMessageSeverity.Error,
                    $"'{rule.Descriptor.Id}' produces {rule.Descriptor.Risk} values and requires run-time acknowledgement.",
                    RuleMessageCode.RiskWarning));
                if (!allowRiskyValues)
                    blocked = true;
            }
            else
            {
                foreach (var message in validation.Messages)
                {
                    if (message.Code != RuleMessageCode.RiskWarning)
                        messages.Add(message);
                }
            }

            compiled.Add((request, rule, RequiresPreflight(rule.Descriptor)));
        }

        if (blocked)
            return BogusRunPreparationResult.Blocked(messages, generatedValueCount: 0);

        var run = new PreparedBogusRun();
        var generated = 0;
        long totalAccounting = 0;

        try
        {
            var locale = compiled.Count > 0 ? compiled[0].Request.Context.Locale : DeterministicFaker.DefaultLocale;
            using var session = new BogusEvaluatorSession(locale);

            foreach (var (request, rule, preflight) in compiled)
            {
                run.AddCompiled(request.TableLogicalName, request.ColumnLogicalName, rule);
                if (!preflight)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                var count = request.Context.RecordCount;
                var cache = new object[count];
                long ruleAccounting = checked((long)count * IntPtr.Size);

                for (var row = 0; row < count; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((row & 15) == 0)
                        await Task.Yield();

                    object value;
                    try
                    {
                        value = session.Evaluate(rule, request.Attribute, request.Context, row);
                    }
                    catch (InvalidOperationException ex)
                    {
                        run.Dispose();
                        messages.Add(new RuleMessage(
                            RuleMessageSeverity.Error, ex.Message, RuleMessageCode.LengthBudget, RuleInputTarget.Length));
                        return BogusRunPreparationResult.Blocked(messages, generated);
                    }

                    if (value is string text)
                    {
                        ruleAccounting = checked(ruleAccounting + (long)text.Length * sizeof(char));
                        if (ruleAccounting > MaxRulePreparationBytes
                            || checked(totalAccounting + ruleAccounting) > MaxTotalPreparationBytes)
                        {
                            run.Dispose();
                            messages.Add(new RuleMessage(
                                RuleMessageSeverity.Error,
                                "Known-input preparation budget exceeded.",
                                RuleMessageCode.LengthBudget,
                                RuleInputTarget.Length));
                            return BogusRunPreparationResult.Blocked(messages, generated);
                        }
                    }

                    cache[row] = value;
                    generated++;
                }

                totalAccounting = checked(totalAccounting + ruleAccounting);
                if (totalAccounting > MaxTotalPreparationBytes)
                {
                    run.Dispose();
                    messages.Add(new RuleMessage(
                        RuleMessageSeverity.Error,
                        "Known-input preparation budget exceeded.",
                        RuleMessageCode.LengthBudget,
                        RuleInputTarget.Length));
                    return BogusRunPreparationResult.Blocked(messages, generated);
                }

                run.AddCache(request.TableLogicalName, request.ColumnLogicalName, cache, request.Context);
            }

            return BogusRunPreparationResult.Succeeded(run, generated, messages);
        }
        catch
        {
            run.Dispose();
            throw;
        }
    }

    private static bool RequiresPreflight(BogusEndpointDescriptor descriptor) =>
        descriptor.RawKind == BogusRawKind.String
        && (descriptor.Output.Policy == BogusLengthPolicy.Unknown || !descriptor.Output.TransportCertified);
}

/// <summary>One column submitted to <see cref="BogusRulePreparer.PrepareRun"/>.</summary>
/// <param name="EffectiveRule">Normalized Bogus rule from validation.</param>
/// <param name="Attribute">Live attribute metadata.</param>
/// <param name="TableLogicalName">Canonical table logical name.</param>
/// <param name="ColumnLogicalName">Canonical column logical name.</param>
/// <param name="Context">Complete evaluation context for this table.</param>
public sealed record BogusPreparationRequest(
    BogusRule EffectiveRule,
    AttributeMetadata Attribute,
    string TableLogicalName,
    string ColumnLogicalName,
    RuleEvaluationContext Context);

/// <summary>Outcome of atomic whole-run Bogus preparation.</summary>
public sealed class BogusRunPreparationResult
{
    private BogusRunPreparationResult(
        bool isBlocked, int generatedValueCount, IReadOnlyList<RuleMessage> messages, PreparedBogusRun? run)
    {
        IsBlocked = isBlocked;
        GeneratedValueCount = generatedValueCount;
        Messages = messages;
        Run = run;
    }

    /// <summary>True when the run must not write. <see cref="Run"/> is then null.</summary>
    public bool IsBlocked { get; }

    /// <summary>Dynamic values generated in pass 2. Zero when pass 1 blocked.</summary>
    public int GeneratedValueCount { get; }

    /// <summary>Validation, risk, and length messages collected for the run.</summary>
    public IReadOnlyList<RuleMessage> Messages { get; }

    /// <summary>Prepared caches when not blocked. Null when blocked.</summary>
    public PreparedBogusRun? Run { get; }

    internal static BogusRunPreparationResult Blocked(IReadOnlyList<RuleMessage> messages, int generatedValueCount) =>
        new(true, generatedValueCount, messages, run: null);

    internal static BogusRunPreparationResult Succeeded(
        PreparedBogusRun run, int generatedValueCount, IReadOnlyList<RuleMessage> messages) =>
        new(false, generatedValueCount, messages, run);
}

/// <summary>
/// Context-bound prepared values for one <c>BulkCreator.CreateAsync</c> attempt.
/// Caches row values only for rules that required exact preflight.
/// </summary>
public sealed class PreparedBogusRun : IDisposable
{
    private readonly Dictionary<EntryKey, PreparedBogusRule> _rules = [];
    private readonly Dictionary<EntryKey, CacheEntry> _caches = [];
    private bool _disposed;

    /// <summary>True when this column has a row-indexed preflight cache.</summary>
    public bool ContainsCache(string tableLogicalName, string columnLogicalName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _caches.ContainsKey(new EntryKey(tableLogicalName, columnLogicalName));
    }

    /// <summary>Returns a cached prepared value after validating context, count, and engine version.</summary>
    /// <param name="tableLogicalName">Canonical table key.</param>
    /// <param name="columnLogicalName">Canonical column key.</param>
    /// <param name="context">Consumption context; table and count must match preparation.</param>
    /// <param name="rowIndex">Zero-based row.</param>
    public object GetValue(
        string tableLogicalName, string columnLogicalName, RuleEvaluationContext context, int rowIndex)
    {
        if (!TryGetValue(tableLogicalName, columnLogicalName, context, rowIndex, out var value) || value is null)
            throw new InvalidOperationException(
                $"No prepared value for '{tableLogicalName}.{columnLogicalName}' row {rowIndex}.");
        return value;
    }

    /// <summary>Tries to read a cached value. Returns false when the column was not preflighted.</summary>
    public bool TryGetValue(
        string tableLogicalName,
        string columnLogicalName,
        RuleEvaluationContext context,
        int rowIndex,
        out object? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(rowIndex);
        value = null;

        var key = new EntryKey(tableLogicalName, columnLogicalName);
        if (!_caches.TryGetValue(key, out var entry))
            return false;

        if (!string.Equals(context.Table, entry.Context.Table, StringComparison.Ordinal)
            || context.RecordCount != entry.Context.RecordCount)
        {
            throw new InvalidOperationException(
                "Prepared cache context does not match the consumption table or record count.");
        }

        if (!_rules.TryGetValue(key, out var rule))
            throw new InvalidOperationException("Prepared cache is missing its compiled rule.");

        rule.EnsureMatches(context);
        if (rowIndex >= entry.Values.Length)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));

        value = entry.Values[rowIndex];
        return true;
    }

    internal void AddCompiled(string table, string column, PreparedBogusRule rule)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _rules[new EntryKey(table, column)] = rule;
    }

    internal void AddCache(string table, string column, object[] values, RuleEvaluationContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _caches[new EntryKey(table, column)] = new CacheEntry(values, context);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _caches.Clear();
        _rules.Clear();
        _disposed = true;
    }

    private readonly record struct EntryKey(string Table, string Column);

    private readonly record struct CacheEntry(object[] Values, RuleEvaluationContext Context);
}

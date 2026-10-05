using SeedBomb.Core.Generators;
using SeedBomb.Core.Rules;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using System.Collections.ObjectModel;
using System.Globalization;

namespace SeedBomb.ViewModels;

/// <summary>Rule-preview sampling: the Rules page's debounced preview and Review's samples (WR-005).</summary>
public sealed class RulePreviewController
{
    private int _generation;
    private CancellationTokenSource? _cts;
    private int _salt;
    private IReadOnlyList<string> _values = [];

    /// <summary>Raised after <see cref="Values"/> is replaced.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Run-time-only preview copy for lookupRandom, shown by the Rules page and Review.
    /// Interpolates the shared candidate bound.
    /// </summary>
    public static string LookupRandomExplanation { get; } =
        $"Uses up to {LookupRandomRule.MaximumCandidatesPerTarget.ToString("N0", CultureInfo.InvariantCulture)} existing records per target, captured before generation. Same seed and captured records give the same picks. Preview is resolved when the run starts. Candidate validation happens at Start before writes.";

    /// <summary>
    /// WR-005: the one rule sampler. Formats rows 0..<paramref name="rows"/>-1 of
    /// <paramref name="effective"/>, or returns the lookupRandom explanation.
    /// </summary>
    /// <param name="effective">A validated effective rule.</param>
    /// <param name="attr">Target column metadata.</param>
    /// <param name="eval">Table, seed, locale, run id and record count to sample with.</param>
    /// <param name="rows">How many rows to sample.</param>
    /// <exception cref="InvalidOperationException">Generated text failed a length or transport-safety check.</exception>
    public static IReadOnlyList<string> Sample(
        FieldRule effective, AttributeMetadata attr, RuleEvaluationContext eval, int rows)
    {
        if (effective is LookupRandomRule)
            return [LookupRandomExplanation];

        var preview = new List<string>(rows);
        if (effective is BogusRule bogus)
        {
            var prepared = BogusRulePreparer.CompileRule(bogus, attr, eval);
            using var session = new BogusEvaluatorSession(eval.Locale);
            for (var row = 0; row < rows; row++)
                preview.Add(Format(session.Evaluate(prepared, attr, eval, row)));
            return preview;
        }

        for (var row = 0; row < rows; row++)
            preview.Add(Format(RuleValueGenerator.Evaluate(effective, attr, eval.Seed, eval.Table, row, eval.RunId)));
        return preview;
    }

    /// <summary>Live preview rows mapped from <see cref="Values"/>.</summary>
    public ObservableCollection<PreviewRow> Rows { get; } = [];

    /// <summary>Formatted preview samples for rows 0..2.</summary>
    public IReadOnlyList<string> Values => _values;

    /// <summary>Bumps the preview salt so the next schedule draws a different substream.</summary>
    public void Reroll() => _salt++;

    /// <summary>Drops in-flight preview work.</summary>
    public void Cancel()
    {
        Interlocked.Increment(ref _generation);
        _cts?.Cancel();
    }

    /// <summary>Clears published samples immediately.</summary>
    public void Clear() => Publish([]);

    /// <summary>Schedules a 150ms-debounced preview of <paramref name="effective"/>.</summary>
    public void Schedule(
        FieldRule? effective,
        AttributeMetadata? attr,
        int seed,
        string table,
        string runId,
        int recordCount)
    {
        var generation = Interlocked.Increment(ref _generation);
        if (effective is null || attr is null)
        {
            if (generation == _generation)
                Publish([]);
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = RunAsync(
            generation, effective, attr, seed + _salt, table, runId, recordCount, _cts.Token);
    }

    private async Task RunAsync(
        int generation,
        FieldRule effective,
        AttributeMetadata attr,
        int seed,
        string table,
        string runId,
        int recordCount,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(150, ct);
            if (generation != _generation)
                return;

            var preview = Sample(
                effective, attr,
                new RuleEvaluationContext(table, seed, DeterministicFaker.DefaultLocale, runId, recordCount), rows: 3);
            if (generation != _generation)
                return;
            Publish(preview);
        }
        catch (OperationCanceledException)
        {
            // superseded edit
        }
        catch (InvalidOperationException)
        {
            if (generation == _generation)
                Publish([]);
        }
    }

    private void Publish(IReadOnlyList<string> preview)
    {
        _values = preview;
        Rows.Clear();
        foreach (var value in preview)
        {
            Rows.Add(IsBlank(value)
                ? new PreviewRow("— blank —", "Blank")
                : new PreviewRow(value, "Value"));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsBlank(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value is "(null)" or "(omitted)";

    private static string Format(object? value)
    {
        if (value is null) return "(null)";
        if (ReferenceEquals(value, RuleValueGenerator.Omit)) return "(omitted)";
        return value switch
        {
            OptionSetValue osv => osv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Money m => m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            EntityReference r => $"{r.LogicalName} · {r.Id:D}",
            _ => value.ToString() ?? string.Empty,
        };
    }
}
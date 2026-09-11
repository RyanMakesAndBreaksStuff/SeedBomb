using System.Collections.ObjectModel;
using DataGen.Core.Generators;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Seedbomb.ViewModels;

/// <summary>Debounced rule-preview samples for the Rules page.</summary>
public sealed class RulePreviewController
{
    private int _generation;
    private CancellationTokenSource? _cts;
    private int _salt;
    private IReadOnlyList<string> _values = [];

    /// <summary>Raised after <see cref="Values"/> is replaced.</summary>
    public event EventHandler? Changed;

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
        int recordCount,
        string lookupRandomExplanation)
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
            generation, effective, attr, seed + _salt, table, runId, recordCount,
            lookupRandomExplanation, _cts.Token);
    }

    private async Task RunAsync(
        int generation,
        FieldRule effective,
        AttributeMetadata attr,
        int seed,
        string table,
        string runId,
        int recordCount,
        string lookupRandomExplanation,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(150, ct);
            if (generation != _generation)
                return;

            var preview = Evaluate(effective, attr, seed, table, runId, recordCount, lookupRandomExplanation);
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

    private static List<string> Evaluate(
        FieldRule effective,
        AttributeMetadata attr,
        int seed,
        string table,
        string runId,
        int recordCount,
        string lookupRandomExplanation)
    {
        if (effective is LookupRandomRule)
            return [lookupRandomExplanation];

        var preview = new List<string>(3);
        var eval = new RuleEvaluationContext(table, seed, DeterministicFaker.DefaultLocale, runId, recordCount);
        if (effective is BogusRule bogus)
        {
            var prepared = BogusRulePreparer.CompileRule(bogus, attr, eval);
            using var session = new BogusEvaluatorSession(eval.Locale);
            for (var row = 0; row < 3; row++)
                preview.Add(Format(session.Evaluate(prepared, attr, eval, row)));
            return preview;
        }

        for (var row = 0; row < 3; row++)
            preview.Add(Format(RuleValueGenerator.Evaluate(effective, attr, seed, table, row, runId)));
        return preview;
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

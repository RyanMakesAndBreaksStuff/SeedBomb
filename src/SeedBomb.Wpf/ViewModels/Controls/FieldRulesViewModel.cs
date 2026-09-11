// src/DataGen.Wpf/ViewModels/Controls/FieldRulesViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DataGen.Core.Rules;

namespace Seedbomb.ViewModels.Controls;

/// <summary>One board row = one active rule (spec: the board lists rules, not columns).</summary>
public sealed record RuleRow(string Table, string Column, string DisplayName, string OpBadge, string ParameterSummary, string Preview, FieldRule Rule);

/// <summary>Single source for draft rule plus board presentation derived by editor/Core preview.</summary>
public sealed record RuleDraftEntry(FieldRule Rule, string DisplayName, string Preview);

/// <summary>
/// Field Rules board state: a draft rule map per table, plus the last committed snapshot (§3.5).
/// Draft mutations never touch the committed copy; Commit promotes, DiscardDraft restores.
/// </summary>
public sealed partial class FieldRulesViewModel : ObservableObject
{
    private Dictionary<string, Dictionary<string, RuleDraftEntry>> _draft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, RuleDraftEntry>> _committed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Changes after every draft mutation; Review snapshot is valid only for matching value.</summary>
    public long Revision { get; private set; }

    /// <summary>True when draft differs from the last committed snapshot (Load/dirty confirm).</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Raised after a draft mutation so GenerateViewModel can invalidate preflight state.</summary>
    public event EventHandler? DraftChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rows))]
    private string _selectedTable = "";

    /// <summary>Rows for the selected table — active rules only; empty list = fully-automatic table (F1b).</summary>
    public ObservableCollection<RuleRow> Rows { get; } = [];

    /// <summary>Selects the table whose rules the board shows.</summary>
    public void SelectTable(string table) { SelectedTable = table; RebuildRows(); }

    /// <summary>Adds or replaces the draft rule for (table, column).</summary>
    public void SetRule(string table, string column, FieldRule rule, string displayName, string preview)
    {
        if (!_draft.TryGetValue(table, out var cols))
            _draft[table] = cols = new(StringComparer.OrdinalIgnoreCase);
        cols[column] = new(rule, displayName, preview);
        MarkChanged();
        RebuildRows();
    }

    /// <summary>Removes the rule — the column quietly returns to auto; empty tables are dropped (S7).</summary>
    public void RemoveRule(string table, string column)
    {
        if (!_draft.TryGetValue(table, out var cols) || !cols.Remove(column))
            return;
        if (cols.Count == 0) _draft.Remove(table);
        MarkChanged();
        RebuildRows();
    }

    /// <summary>Draft rules in <c>GenerationConfig.FieldRules</c> shape. Empty map ⇒ pass null to config.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, FieldRule>> GetRules() =>
        _draft.ToDictionary(
            table => table.Key,
            table => table.Value.ToDictionary(column => column.Key, column => column.Value.Rule, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Promotes the draft to committed (Start pressed — the first committing action, S2).</summary>
    public void Commit()
    {
        _committed = Clone(_draft);
        IsDirty = false;
    }

    /// <summary>Discards the draft and restores the previously committed configuration verbatim (S2).</summary>
    public void DiscardDraft()
    {
        _draft = Clone(_committed);
        IsDirty = false;
        Revision++;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        RebuildRows();
    }

    /// <summary>Clears draft and committed snapshot as one step so <see cref="DiscardDraft"/> cannot resurrect rules.</summary>
    public void HardReset()
    {
        _draft = new(StringComparer.OrdinalIgnoreCase);
        _committed = new(StringComparer.OrdinalIgnoreCase);
        IsDirty = false;
        Revision++;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        RebuildRows();
    }

    /// <summary>
    /// Replaces the entire draft with <paramref name="rules"/> (Load / Import → board).
    /// One revision bump; board receives <c>EffectiveRule</c> values only.
    /// </summary>
    public void ReplaceDraft(IReadOnlyDictionary<string, Dictionary<string, RuleDraftEntry>> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _draft = rules.ToDictionary(
            table => table.Key,
            table => new Dictionary<string, RuleDraftEntry>(table.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        MarkChanged();
        RebuildRows();
    }

    private void MarkChanged()
    {
        Revision++;
        IsDirty = true;
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildRows()
    {
        Rows.Clear();
        if (_draft.TryGetValue(SelectedTable, out var cols))
            foreach (var (column, entry) in cols.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                Rows.Add(new RuleRow(SelectedTable, column, entry.DisplayName, OpBadge(entry.Rule),
                    Summary(entry.Rule), entry.Preview, entry.Rule));
    }

    private static Dictionary<string, Dictionary<string, RuleDraftEntry>> Clone(
        Dictionary<string, Dictionary<string, RuleDraftEntry>> src) =>
        src.ToDictionary(
            table => table.Key,
            table => new Dictionary<string, RuleDraftEntry>(table.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static string OpBadge(FieldRule r) => r switch
    {
        ConstantRule => "constant",
        OneOfRule => "one-of",
        RangeRule => "range",
        PatternRule => "pattern",
        SequenceRule => "sequence",
        NullRule => "null",
        LookupRandomRule => "random lookup",
        BogusRule => "bogus",
        _ => "?",
    };

    private static string Summary(FieldRule r) => r switch
    {
        ConstantRule c => FormatValue(c.Value),
        OneOfRule o => $"{{ {string.Join(", ", o.Values.Select(FormatValue))} }} · pick: {o.Pick.ToString().ToLowerInvariant()}",
        RangeRule g => $"{g.Min} – {g.Max}",
        PatternRule p => p.Template,
        SequenceRule s => $"start {s.Start} · step {s.Step}",
        NullRule => "leave unset",
        BogusRule b => $"{b.Api}.{b.Endpoint}",
        LookupRandomRule =>
            $"up to {LookupRandomRule.MaximumCandidatesPerTarget.ToString("N0", CultureInfo.InvariantCulture)} existing records per target",
        _ => "",
    };

    private static string FormatValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return value.ToString();

        string? name = null;
        string? entity = null;
        string? id = null;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                continue;
            if (property.NameEquals("name") && property.Value.ValueKind == JsonValueKind.String)
                name = property.Value.GetString();
            else if (property.NameEquals("entity") && property.Value.ValueKind == JsonValueKind.String)
                entity = property.Value.GetString();
            else if (property.NameEquals("id") && property.Value.ValueKind == JsonValueKind.String)
                id = property.Value.GetString();
        }

        if (entity is null || id is null)
            return value.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"{entity} · {id}" : $"{name} · {entity} · {id}";
    }
}

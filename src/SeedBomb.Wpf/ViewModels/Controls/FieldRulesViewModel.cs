// src/SeedBomb.Wpf/ViewModels/Controls/FieldRulesViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;
using SeedBomb.Core.Rules;

namespace SeedBomb.ViewModels.Controls;

/// <summary>Single source for a draft rule.</summary>
public sealed record RuleDraftEntry(FieldRule Rule);

/// <summary>
/// Field Rules board state: a draft rule map per table (§3.5).
/// </summary>
public sealed partial class FieldRulesViewModel : ObservableObject
{
    private Dictionary<string, Dictionary<string, RuleDraftEntry>> _draft = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Changes after every draft mutation; Review snapshot is valid only for matching value.</summary>
    public long Revision { get; private set; }

    /// <summary>True when draft differs from the last committed snapshot (Load/dirty confirm).</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Raised after a draft mutation so GenerateViewModel can invalidate preflight state.</summary>
    public event EventHandler? DraftChanged;

    /// <summary>Adds or replaces the draft rule for (table, column).</summary>
    public void SetRule(string table, string column, FieldRule rule, string displayName, string preview)
    {
        if (!_draft.TryGetValue(table, out var cols))
            _draft[table] = cols = new(StringComparer.OrdinalIgnoreCase);
        cols[column] = new(rule);
        MarkChanged();
    }

    /// <summary>Removes the rule — the column quietly returns to auto; empty tables are dropped (S7).</summary>
    public void RemoveRule(string table, string column)
    {
        if (!_draft.TryGetValue(table, out var cols) || !cols.Remove(column))
            return;
        if (cols.Count == 0) _draft.Remove(table);
        MarkChanged();
    }

    /// <summary>Drops every table's rules except <paramref name="tables"/> — a deselected table leaves the board.</summary>
    public void RetainTables(IEnumerable<string> tables)
    {
        var keep = tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dropped = _draft.Keys.Where(t => !keep.Contains(t)).ToList();
        if (dropped.Count == 0)
            return;
        foreach (var table in dropped)
            _draft.Remove(table);
        MarkChanged();
    }

    /// <summary>Draft rules in <c>GenerationConfig.FieldRules</c> shape. Empty map ⇒ pass null to config.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, FieldRule>> GetRules() =>
        _draft.ToDictionary(
            table => table.Key,
            table => table.Value.ToDictionary(column => column.Key, column => column.Value.Rule,
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks the draft as committed (Start pressed).</summary>
    public void Commit() => IsDirty = false;

    /// <summary>Clears the draft.</summary>
    public void HardReset()
    {
        _draft = new(StringComparer.OrdinalIgnoreCase);
        IsDirty = false;
        Revision++;
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the entire draft with <paramref name="rules"/> (Load / Import → board).
    /// One revision bump.
    /// </summary>
    public void ReplaceDraft(IReadOnlyDictionary<string, Dictionary<string, RuleDraftEntry>> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _draft = rules.ToDictionary(
            table => table.Key,
            table => new Dictionary<string, RuleDraftEntry>(table.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        MarkChanged();
    }

    private void MarkChanged()
    {
        Revision++;
        IsDirty = true;
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }
}

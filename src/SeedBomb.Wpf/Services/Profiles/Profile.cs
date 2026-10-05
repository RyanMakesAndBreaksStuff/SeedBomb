using SeedBomb.Core.Rules;

namespace SeedBomb.Services.Profiles;

/// <summary>
/// Profile schema: a named, saveable rule configuration — selected tables,
/// per-table record counts, active field rules, and an optional pinned seed. Plain JSON,
/// contains no credentials by schema (D2); loaded/saved only through
/// <see cref="IProfileService"/>. The app never renders this as raw text or a schema editor.
/// Writers emit <see cref="CurrentProfileVersion"/>.
/// </summary>
public sealed record Profile(
    int ProfileVersion,
    string Name,
    string? Description,
    int? Seed,
    IReadOnlyList<ProfileTable> Tables)
{
    /// <summary>Version written by every save, export, draft, and import path.</summary>
    public const int CurrentProfileVersion = 2;

    /// <summary>
    /// Copy of this profile with one column rule set, or removed when <paramref name="rule"/> is null.
    /// Never mutates this instance, so a failed save leaves the committed profile intact (WR-002).
    /// </summary>
    public Profile WithColumnRule(string table, string column, FieldRule? rule) => this with
    {
        Tables = Tables.Select(t =>
        {
            if (!string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase))
                return t;
            var cols = t.Columns is null
                ? new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FieldRule>(t.Columns, StringComparer.OrdinalIgnoreCase);
            if (rule is null)
                cols.Remove(column);
            else
                cols[column] = rule;
            return t with { Columns = cols };
        }).ToList(),
    };
}

/// <summary>
/// One table entry inside a <see cref="Profile"/>: target table logical name, row count, and
/// active field rules keyed by column logical name. Absent/empty <see cref="Columns"/> means the
/// table is fully automatic — today's inference path, byte-identical (S7).
/// </summary>
public sealed record ProfileTable(
    string Table,
    int Count,
    Dictionary<string, FieldRule>? Columns);
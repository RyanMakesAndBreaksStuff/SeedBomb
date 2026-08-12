using DataGen.Core.Rules;

namespace Seedbomb.Services.Profiles;

/// <summary>
/// Profile schema v1 (§08): a named, saveable rule configuration — selected tables,
/// per-table record counts, active field rules, and an optional pinned seed. Plain JSON,
/// contains no credentials by schema (D2); loaded/saved only through
/// <see cref="IProfileService"/>. The app never renders this as raw text or a schema editor.
/// </summary>
public sealed record Profile(
    int ProfileVersion,
    string Name,
    string? Description,
    int? Seed,
    IReadOnlyList<ProfileTable> Tables);

/// <summary>
/// One table entry inside a <see cref="Profile"/>: target table logical name, row count, and
/// active field rules keyed by column logical name. Absent/empty <see cref="Columns"/> means the
/// table is fully automatic — today's inference path, byte-identical (S7).
/// </summary>
public sealed record ProfileTable(
    string Table,
    int Count,
    Dictionary<string, FieldRule>? Columns);

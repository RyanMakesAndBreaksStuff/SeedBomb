namespace SeedBomb.Core.Contracts;

/// <summary>
/// Configuration for a data generation run.
/// </summary>
public record GenerationConfig
{
    /// <summary>
    /// Gets the logical names of entities to generate data for.
    /// </summary>
    public required string[] EntityLogicalNames { get; init; }

    /// <summary>
    /// Gets the number of records to generate per entity.
    /// </summary>
    public required Dictionary<string, int> RecordCounts { get; init; }

    /// <summary>
    /// Gets the base seed for deterministic generation. Default is 42.
    /// </summary>
    public int Seed { get; init; } = 42;

    /// <summary>
    /// Bogus locale for this run. Default is <c>en</c>. Not per-rule.
    /// The WPF surface is read-only until a locale picker ships.
    /// </summary>
    public string Locale { get; init; } = "en";

    /// <summary>Run-scoped opt-in for risky Bogus endpoints. Never persisted to profile or retry state.</summary>
    public bool AllowRiskyBogusValues { get; init; }

    /// <summary>
    /// Gets the batch size for bulk creation. Default is 500.
    /// </summary>
    public int BatchSize { get; init; } = GenerationLimits.DefaultBatchSize;

    /// <summary>
    /// Gets the maximum degree of parallelism. Null means system default.
    /// </summary>
    public int? MaxParallelism { get; init; }

    /// <summary>
    /// Gets the maximum number of retries per batch. Default is 3.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Per-table field rules keyed by [entityLogicalName][attributeLogicalName].
    /// Null or missing table/column ⇒ legacy inference, byte-identically (spec S7).
    /// Rules must be pre-validated via <see cref="Rules.RuleValidator"/> before a run.
    /// </summary>
    public Dictionary<string, Dictionary<string, Rules.FieldRule>>? FieldRules { get; init; }

    /// <summary>Run stamp substituted for the {runId} pattern token. Never auto-injected into data (D4).</summary>
    public string RunId { get; init; } = "";

    /// <summary>Planned rows for <paramref name="table"/>, or 0 when the table is absent or negative.</summary>
    /// <param name="table">Entity logical name.</param>
    public int PlannedRows(string table) => Math.Max(0, RecordCounts.GetValueOrDefault(table));

    /// <summary>Sum of <see cref="PlannedRows"/> across <see cref="EntityLogicalNames"/>.</summary>
    public int PlannedTotal => EntityLogicalNames.Sum(PlannedRows);
}

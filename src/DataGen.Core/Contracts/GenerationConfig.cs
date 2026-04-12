namespace DataGen.Core.Contracts;

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
    /// Gets the batch size for bulk creation. Default is 500.
    /// </summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// Gets the maximum degree of parallelism. Null means system default.
    /// </summary>
    public int? MaxParallelism { get; init; }

    /// <summary>
    /// Gets the maximum number of retries per batch. Default is 3.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Gets the optional SignalR connection ID for progress reporting.
    /// </summary>
    public string? ConnectionId { get; init; }
}

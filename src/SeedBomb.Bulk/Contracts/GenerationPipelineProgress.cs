namespace DataGen.Bulk.Contracts;

/// <summary>
/// Reports high-level orchestration and bulk creation progress for a generation run.
/// </summary>
public sealed record GenerationPipelineProgress
{
    /// <summary>Gets the current pipeline phase.</summary>
    public required string Phase { get; init; }

    /// <summary>Gets the entity logical name currently being processed, if any.</summary>
    public string EntityLogicalName { get; init; } = string.Empty;

    /// <summary>Gets the cumulative records created for the current entity.</summary>
    public int RecordsCreated { get; init; }

    /// <summary>Gets the total records expected for the current entity.</summary>
    public int TotalRecords { get; init; }

    /// <summary>Gets the current 1-based batch index for the current entity.</summary>
    public int BatchIndex { get; init; }

    /// <summary>Gets the total batch count for the current entity.</summary>
    public int TotalBatches { get; init; }

    /// <summary>Gets the current throughput in records per minute.</summary>
    public double RecordsPerMinute { get; init; }

    /// <summary>Gets an error message for the current progress update, if any.</summary>
    public string? ErrorMessage { get; init; }
}

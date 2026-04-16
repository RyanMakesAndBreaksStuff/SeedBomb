namespace DataGen.Bulk.Contracts;

/// <summary>
/// Reports progress for a bulk data generation run.
/// </summary>
public record BulkCreationProgress
{
    /// <summary>
    /// Gets the entity logical name currently being processed.
    /// </summary>
    public required string EntityLogicalName { get; init; }

    /// <summary>
    /// Gets the index of the batch just submitted (1-based).
    /// </summary>
    public required int BatchIndex { get; init; }

    /// <summary>
    /// Gets the total number of batches for this entity.
    /// </summary>
    public required int TotalBatches { get; init; }

    /// <summary>
    /// Gets the cumulative number of records created for this entity so far.
    /// </summary>
    public required int RecordsCreated { get; init; }

    /// <summary>
    /// Gets the total number of records to create for this entity.
    /// </summary>
    public required int TotalRecords { get; init; }

    /// <summary>
    /// Gets the throughput in records per minute for the most recent batch.
    /// </summary>
    public double RecordsPerMinute { get; init; }

    /// <summary>
    /// Gets an error message if the batch failed, or null on success.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

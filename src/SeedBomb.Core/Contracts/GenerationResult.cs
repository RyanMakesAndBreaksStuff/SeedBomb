namespace SeedBomb.Core.Contracts;

/// <summary>
/// The result of a data generation run including created records and any errors.
/// </summary>
public record GenerationResult
{
    /// <summary>
    /// Gets the records created per entity, keyed by entity logical name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Guid>> CreatedRecords { get; init; }
        = new Dictionary<string, IReadOnlyList<Guid>>();

    /// <summary>
    /// Gets the total number of records created across all entities.
    /// </summary>
    public int TotalRecords => CreatedRecords.Values.Sum(v => v.Count);

    /// <summary>
    /// Gets the total elapsed time for the generation run.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Gets any errors that occurred during generation.
    /// </summary>
    public IReadOnlyList<BatchError> Errors { get; init; } = [];

    /// <summary>
    /// Gets the number of rows the run lost across all <see cref="Errors"/> (a failed batch counts every row in it).
    /// </summary>
    public int RejectedRows => Errors.Sum(e => e.RowCount);

    /// <summary>
    /// Gets whether the run was cancelled before it finished. <see cref="CreatedRecords"/> then holds
    /// only the rows written before the cancel; they are not rolled back.
    /// </summary>
    public bool Cancelled { get; init; }

    /// <summary>
    /// Set when the run stopped on an error after at least one row was written.
    /// <see cref="CreatedRecords"/> then holds only the rows written before the failure; they are not rolled back.
    /// </summary>
    public string? FatalError { get; init; }
}

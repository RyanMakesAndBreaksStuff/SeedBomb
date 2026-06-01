namespace DataGen.Wpf.Services.History;

/// <summary>
/// Persisted record of a completed generation run.
/// </summary>
/// <param name="Id">Unique run identifier.</param>
/// <param name="Timestamp">When the run started.</param>
/// <param name="EntityNames">Display names of entities that were generated.</param>
/// <param name="TotalRecords">Total number of records created.</param>
/// <param name="Duration">Elapsed time for the run.</param>
/// <param name="Succeeded">Whether the run completed without fatal errors.</param>
/// <param name="ErrorCount">Number of batch errors encountered.</param>
public record RunRecord(
    Guid Id,
    DateTimeOffset Timestamp,
    string[] EntityNames,
    int TotalRecords,
    TimeSpan Duration,
    bool Succeeded,
    int ErrorCount);

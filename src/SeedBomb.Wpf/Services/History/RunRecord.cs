namespace Seedbomb.Services.History;

/// <summary>
/// Persisted record of a completed generation run.
/// </summary>
/// <param name="Id">Unique run identifier.</param>
/// <param name="Timestamp">When the run started.</param>
/// <param name="EntityNames">Display names of entities that were generated.</param>
/// <param name="TotalRecords">Total number of records created.</param>
/// <param name="Duration">Elapsed time for the run.</param>
/// <param name="Succeeded">Whether the run completed without fatal errors.</param>
/// <param name="ErrorCount">Rows rejected during the run. Records written before 15 Sep 2026 hold a batch count instead.</param>
/// <param name="Environment">Host of the environment the run targeted, or "" when unknown.</param>
/// <param name="User">Signed-in user for the run, or "" when unknown.</param>
/// <param name="Profile">Rule profile active for the run, or "" when unknown.</param>
public record RunRecord(
    Guid Id,
    DateTimeOffset Timestamp,
    string[] EntityNames,
    int TotalRecords,
    TimeSpan Duration,
    bool Succeeded,
    int ErrorCount,
    string Environment = "",
    string User = "",
    string Profile = "");

namespace DataGen.Web.Hubs;

/// <summary>Carries real-time generation progress data pushed to clients via SignalR.</summary>
/// <param name="Phase">Current generation phase (e.g. "Generating", "Backfilling").</param>
/// <param name="EntityName">The entity currently being processed.</param>
/// <param name="RecordsCreated">Records successfully created so far.</param>
/// <param name="TotalRecords">Total records to create across all entities.</param>
/// <param name="BatchesCompleted">Number of batches completed for current entity.</param>
/// <param name="TotalBatches">Total batches for current entity.</param>
/// <param name="RecordsPerMinute">Current throughput.</param>
/// <param name="Elapsed">Total elapsed time since generation started.</param>
public record ProgressUpdate(
    string Phase,
    string EntityName,
    int RecordsCreated,
    int TotalRecords,
    int BatchesCompleted,
    int TotalBatches,
    double RecordsPerMinute,
    TimeSpan Elapsed);

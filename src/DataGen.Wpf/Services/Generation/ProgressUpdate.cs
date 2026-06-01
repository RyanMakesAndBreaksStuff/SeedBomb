namespace DataGen.Wpf.Services.Generation;

/// <summary>
/// Immutable progress snapshot reported from the generation pipeline to the WPF UI.
/// </summary>
/// <param name="Phase">Current pipeline phase label (e.g. "Inspecting schema", "Generating records").</param>
/// <param name="EntityName">Logical name of the entity currently being processed.</param>
/// <param name="RecordsCreated">Records created so far for the current entity.</param>
/// <param name="TotalRecords">Total records expected for the current entity.</param>
/// <param name="BatchesCompleted">Batches completed so far across all entities.</param>
/// <param name="TotalBatches">Total batch count across all entities.</param>
/// <param name="RecordsPerMinute">Instantaneous throughput rate.</param>
/// <param name="Elapsed">Total wall-clock time since generation started.</param>
public sealed record ProgressUpdate(
    string Phase,
    string EntityName,
    int RecordsCreated,
    int TotalRecords,
    int BatchesCompleted,
    int TotalBatches,
    double RecordsPerMinute,
    TimeSpan Elapsed);

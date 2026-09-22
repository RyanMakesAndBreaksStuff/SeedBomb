namespace SeedBomb.Web.Services;

/// <summary>
/// Immutable progress snapshot reported from the generation pipeline to the Blazor UI.
/// </summary>
public sealed record ProgressUpdate(
    string Phase,
    string EntityName,
    int RecordsCreated,
    int TotalRecords,
    int BatchesCompleted,
    int TotalBatches,
    double RecordsPerMinute,
    TimeSpan Elapsed);

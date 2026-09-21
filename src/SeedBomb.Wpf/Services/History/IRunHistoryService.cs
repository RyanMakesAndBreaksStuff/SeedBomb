namespace SeedBomb.Services.History;

/// <summary>
/// Persists and retrieves generation run history.
/// </summary>
public interface IRunHistoryService
{
    /// <summary>Prepends a new run record to history.</summary>
    Task AddRunAsync(RunRecord run, CancellationToken ct = default);

    /// <summary>Returns all run records, most recent first.</summary>
    Task<IReadOnlyList<RunRecord>> GetRunsAsync(CancellationToken ct = default);

    /// <summary>Removes all stored run records.</summary>
    Task ClearAsync(CancellationToken ct = default);
}
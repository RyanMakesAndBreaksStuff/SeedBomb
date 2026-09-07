namespace Seedbomb.Services.Connections;

/// <summary>Persists and retrieves Dataverse connection profiles.</summary>
public interface IConnectionProfileService
{
    /// <summary>Raised after any profile is saved or deleted.</summary>
    event EventHandler ProfilesChanged;

    /// <summary>Returns all stored profiles.</summary>
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Adds or updates a profile (matched by <see cref="ConnectionProfile.Id"/>).</summary>
    Task SaveAsync(ConnectionProfile profile, CancellationToken ct = default);

    /// <summary>Removes the profile with the given <paramref name="id"/>.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns the most-recently-used profile, or the first profile when no
    /// last-used ID is recorded.  Returns <see langword="null"/> when the store is empty.
    /// </summary>
    Task<ConnectionProfile?> GetLastUsedAsync(CancellationToken ct = default);

    /// <summary>Records <paramref name="id"/> as the most-recently-used profile.</summary>
    Task SetLastUsedAsync(Guid id, CancellationToken ct = default);
}

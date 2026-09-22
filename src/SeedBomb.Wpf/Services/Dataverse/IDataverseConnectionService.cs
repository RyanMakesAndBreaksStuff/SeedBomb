using Microsoft.PowerPlatform.Dataverse.Client;

namespace SeedBomb.Services.Dataverse;

/// <summary>
/// Manages a cached <see cref="IOrganizationServiceAsync2"/> connection to Dataverse.
/// </summary>
public interface IDataverseConnectionService
{
    /// <summary>
    /// Raised after the cached Dataverse connection has been reset.
    /// </summary>
    event EventHandler? ConnectionReset;

    /// <summary>
    /// Returns a ready-to-use <see cref="IOrganizationServiceAsync2"/>, building one if needed.
    /// </summary>
    Task<IOrganizationServiceAsync2> GetOrganizationServiceAsync(CancellationToken ct = default);

    /// <summary>
    /// Disposes the cached connection so the next call to <see cref="GetOrganizationServiceAsync"/>
    /// rebuilds it. Call when org URL or credentials change. Safe to await from the UI thread.
    /// </summary>
    Task ResetAsync();
}
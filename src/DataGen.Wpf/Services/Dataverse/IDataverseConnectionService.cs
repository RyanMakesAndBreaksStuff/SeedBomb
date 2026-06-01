using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataGen.Wpf.Services.Dataverse;

/// <summary>
/// Manages a cached <see cref="IOrganizationServiceAsync2"/> connection to Dataverse.
/// </summary>
public interface IDataverseConnectionService
{
    /// <summary>
    /// Returns a ready-to-use <see cref="IOrganizationServiceAsync2"/>, building one if needed.
    /// </summary>
    Task<IOrganizationServiceAsync2> GetOrganizationServiceAsync(CancellationToken ct = default);

    /// <summary>
    /// Disposes the cached connection so the next call to <see cref="GetOrganizationServiceAsync"/>
    /// rebuilds it. Call when org URL or credentials change.
    /// </summary>
    void Reset();
}

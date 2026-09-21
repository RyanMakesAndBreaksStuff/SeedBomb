using Microsoft.PowerPlatform.Dataverse.Client;

namespace SeedBomb.Web.Services;

/// <summary>IServiceClientFactory creates per-circuit Dataverse ServiceClient instances.</summary>
public interface IServiceClientFactory : IAsyncDisposable
{
    /// <summary>Returns the ServiceClient for the current circuit, creating it if needed.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<ServiceClient> CreateAsync(CancellationToken ct = default);
}

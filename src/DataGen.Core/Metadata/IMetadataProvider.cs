using DataGen.Core.Contracts;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Metadata;

/// <summary>
/// Provides access to Dataverse entity metadata with caching.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>
    /// Retrieves metadata for a single entity by logical name.
    /// </summary>
    /// <param name="logicalName">The entity logical name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The entity metadata.</returns>
    Task<EntityMetadata> GetEntityAsync(string logicalName, CancellationToken ct = default);

    /// <summary>
    /// Retrieves metadata for multiple entities by logical name.
    /// </summary>
    /// <param name="logicalNames">The entity logical names.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The entity metadata list.</returns>
    Task<IReadOnlyList<EntityMetadata>> GetEntitiesAsync(string[] logicalNames, CancellationToken ct = default);

    /// <summary>
    /// Lists all user-owned and organization-owned entities in the environment.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summaries of available entities.</returns>
    Task<IReadOnlyList<EntitySummary>> ListUserEntitiesAsync(CancellationToken ct = default);
}

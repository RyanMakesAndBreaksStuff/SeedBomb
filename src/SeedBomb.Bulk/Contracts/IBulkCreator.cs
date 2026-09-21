using DataGen.Core.Contracts;
using DataGen.Core.Graph;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Bulk.Contracts;

/// <summary>
/// Creates large volumes of synthetic Dataverse records in topological dependency order,
/// using CreateMultiple where available and ExecuteMultiple as a fallback.
/// </summary>
public interface IBulkCreator
{
    /// <summary>
    /// Generates and creates records for all requested entities in the correct dependency order.
    /// Deferred lookups (cycle-broken edges) are backfilled in a second pass.
    /// </summary>
    /// <param name="config">Generation configuration (batch size, seed, parallelism, retries).</param>
    /// <param name="entityMetadata">Metadata keyed by entity logical name for all selected entities.</param>
    /// <param name="graph">Dependency graph with topological ordering and deferred edge info.</param>
    /// <param name="progress">Optional progress reporter for real-time feedback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="GenerationResult"/> with all created record IDs and any batch errors.</returns>
    Task<GenerationResult> CreateAsync(
        GenerationConfig config,
        IReadOnlyDictionary<string, EntityMetadata> entityMetadata,
        DependencyGraph graph,
        IProgress<BulkCreationProgress>? progress = null,
        CancellationToken ct = default);
}

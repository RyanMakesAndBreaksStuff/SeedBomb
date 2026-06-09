using DataGen.Core.Contracts;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataGen.Bulk.Contracts;

/// <summary>
/// Orchestrates metadata loading, dependency graph preparation, and bulk record creation.
/// </summary>
public interface IGenerationPipeline
{
    /// <summary>
    /// Executes a full generation run for the selected entities.
    /// </summary>
    /// <param name="config">Generation configuration.</param>
    /// <param name="service">Dataverse organization service used for write operations.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed generation result.</returns>
    Task<GenerationResult> GenerateAsync(
        GenerationConfig config,
        IOrganizationServiceAsync2 service,
        IProgress<GenerationPipelineProgress>? progress = null,
        CancellationToken ct = default);
}

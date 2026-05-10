using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;

namespace DataGen.Web.Services;

/// <summary>
/// Orchestrates the full generation pipeline: schema inspection → dependency resolution → bulk creation.
/// Progress is reported via an <see cref="IProgress{T}"/> callback for Blazor circuit updates.
/// </summary>
public sealed class GenerationService
{
    private readonly IMetadataProvider _metadata;
    private readonly GraphBuilder _graphBuilder;
    private readonly CycleDetector _cycleDetector;
    private readonly TopologicalSort _topoSort;
    private readonly IBulkCreator _bulkCreator;
    private readonly ILogger<GenerationService> _logger;

    /// <summary>Initializes a new instance of <see cref="GenerationService"/>.</summary>
    public GenerationService(
        IMetadataProvider metadata,
        GraphBuilder graphBuilder,
        CycleDetector cycleDetector,
        TopologicalSort topoSort,
        IBulkCreator bulkCreator,
        ILogger<GenerationService> logger)
    {
        _metadata = metadata;
        _graphBuilder = graphBuilder;
        _cycleDetector = cycleDetector;
        _topoSort = topoSort;
        _bulkCreator = bulkCreator;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full generation pipeline for the specified <paramref name="config"/>.
    /// </summary>
    /// <param name="config">Entity selection, record counts, seed, and batch settings.</param>
    /// <param name="uiProgress">Optional callback invoked on each phase/batch completion for Blazor UI updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Generation result containing created record IDs, elapsed time, and any batch errors.</returns>
    public async Task<GenerationResult> GenerateAsync(
        GenerationConfig config,
        IProgress<ProgressUpdate>? uiProgress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            uiProgress?.Report(new ProgressUpdate("Inspecting schema", string.Empty, 0, 0, 0, 0, 0, TimeSpan.Zero));

            var metadataList = await _metadata.GetEntitiesAsync(config.EntityLogicalNames, ct);
            var metadataDict = metadataList.ToDictionary(e => e.LogicalName);

            uiProgress?.Report(new ProgressUpdate("Resolving dependencies", string.Empty, 0, 0, 0, 0, 0, TimeSpan.Zero));

            var graph = _graphBuilder.Build(metadataDict);
            var cycles = _cycleDetector.FindStronglyConnectedComponents(graph);
            if (cycles.Count > 0)
            {
                _logger.LogWarning("Breaking {Count} dependency cycle(s) before generation", cycles.Count);
                _cycleDetector.BreakCycles(graph, cycles, metadataDict);
            }

            uiProgress?.Report(new ProgressUpdate("Generating records", string.Empty, 0, 0, 0, 0, 0, TimeSpan.Zero));

            var bulkProgress = new Progress<BulkCreationProgress>(p =>
            {
                uiProgress?.Report(new ProgressUpdate(
                    Phase: "Generating",
                    EntityName: p.EntityLogicalName,
                    RecordsCreated: p.RecordsCreated,
                    TotalRecords: p.TotalRecords,
                    BatchesCompleted: p.BatchIndex,
                    TotalBatches: p.TotalBatches,
                    RecordsPerMinute: p.RecordsPerMinute,
                    Elapsed: sw.Elapsed));
            });

            var result = await _bulkCreator.CreateAsync(config, metadataDict, graph, bulkProgress, ct);

            _logger.LogInformation(
                "Generation complete — {Total} records in {Elapsed}",
                result.TotalRecords,
                sw.Elapsed);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Generation cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation failed");
            throw;
        }
    }
}

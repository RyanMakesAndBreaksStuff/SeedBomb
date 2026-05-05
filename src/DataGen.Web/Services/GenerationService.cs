using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;
using DataGen.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DataGen.Web.Services;

/// <summary>
/// Orchestrates the full generation pipeline: schema inspection → dependency resolution → bulk creation.
/// Progress is reported via an <see cref="IProgress{T}"/> callback (for Blazor circuit updates)
/// and pushed to SignalR group <c>sessionId</c> (for external observers).
/// </summary>
public sealed class GenerationService
{
    private readonly IMetadataProvider _metadata;
    private readonly GraphBuilder _graphBuilder;
    private readonly CycleDetector _cycleDetector;
    private readonly TopologicalSort _topoSort;
    private readonly IBulkCreator _bulkCreator;
    private readonly IHubContext<ProgressHub> _progressHub;
    private readonly ILogger<GenerationService> _logger;

    /// <summary>Initializes a new instance of <see cref="GenerationService"/>.</summary>
    public GenerationService(
        IMetadataProvider metadata,
        GraphBuilder graphBuilder,
        CycleDetector cycleDetector,
        TopologicalSort topoSort,
        IBulkCreator bulkCreator,
        IHubContext<ProgressHub> progressHub,
        ILogger<GenerationService> logger)
    {
        _metadata = metadata;
        _graphBuilder = graphBuilder;
        _cycleDetector = cycleDetector;
        _topoSort = topoSort;
        _bulkCreator = bulkCreator;
        _progressHub = progressHub;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full generation pipeline for the specified <paramref name="config"/>.
    /// </summary>
    /// <param name="config">Entity selection, record counts, seed, and batch settings.</param>
    /// <param name="sessionId">SignalR group name for push notifications to external observers.</param>
    /// <param name="uiProgress">Optional callback invoked on each batch completion for Blazor UI updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Generation result containing created record IDs, elapsed time, and any batch errors.</returns>
    public async Task<GenerationResult> GenerateAsync(
        GenerationConfig config,
        string sessionId,
        IProgress<ProgressUpdate>? uiProgress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await PushPhaseAsync(sessionId, uiProgress, "Inspecting schema", ct);

            var metadataList = await _metadata.GetEntitiesAsync(config.EntityLogicalNames, ct);
            var metadataDict = metadataList.ToDictionary(e => e.LogicalName);

            await PushPhaseAsync(sessionId, uiProgress, "Resolving dependencies", ct);

            var graph = _graphBuilder.Build(metadataDict);
            var cycles = _cycleDetector.FindStronglyConnectedComponents(graph);
            if (cycles.Count > 0)
            {
                _logger.LogWarning("Breaking {Count} dependency cycle(s) before generation", cycles.Count);
                _cycleDetector.BreakCycles(graph, cycles, metadataDict);
            }

            await PushPhaseAsync(sessionId, uiProgress, "Generating records", ct);

            var bulkProgress = new Progress<BulkCreationProgress>(p =>
            {
                var update = new ProgressUpdate(
                    Phase: "Generating",
                    EntityName: p.EntityLogicalName,
                    RecordsCreated: p.RecordsCreated,
                    TotalRecords: p.TotalRecords,
                    BatchesCompleted: p.BatchIndex,
                    TotalBatches: p.TotalBatches,
                    RecordsPerMinute: p.RecordsPerMinute,
                    Elapsed: sw.Elapsed);

                uiProgress?.Report(update);
                PushToGroup(sessionId, HubMethods.OnProgress, update);
            });

            var result = await _bulkCreator.CreateAsync(config, metadataDict, graph, bulkProgress, ct);

            _logger.LogInformation(
                "Generation complete — {Total} records in {Elapsed}",
                result.TotalRecords,
                sw.Elapsed);

            PushToGroup(sessionId, HubMethods.OnComplete, result.TotalRecords);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Generation cancelled for session {SessionId}", sessionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation failed for session {SessionId}", sessionId);
            PushToGroup(sessionId, HubMethods.OnError, ex.ToString());
            throw;
        }
    }

    private async Task PushPhaseAsync(
        string sessionId,
        IProgress<ProgressUpdate>? uiProgress,
        string phase,
        CancellationToken ct)
    {
        var update = new ProgressUpdate(phase, string.Empty, 0, 0, 0, 0, 0, TimeSpan.Zero);
        uiProgress?.Report(update);
        try
        {
            await _progressHub.Clients.Group(sessionId).SendAsync(HubMethods.OnPhaseChange, phase, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR phase push failed for session {SessionId}", sessionId);
        }
    }

    private void PushToGroup(string sessionId, string method, object? arg = null)
    {
        _ = _progressHub.Clients.Group(sessionId).SendAsync(method, arg)
            .ContinueWith(
                t => _logger.LogWarning(t.Exception, "SignalR push failed for session {SessionId} method {Method}", sessionId, method),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}

using DataGen.Bulk;
using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataGen.Web.Services;

/// <summary>
/// Orchestrates the full generation pipeline: schema inspection → dependency resolution → bulk creation.
/// Progress is reported via an <see cref="IProgress{T}"/> callback for Blazor circuit updates.
/// </summary>
public sealed class GenerationService
{
    private readonly IMetadataProvider? _metadata;
    private readonly GraphBuilder? _graphBuilder;
    private readonly CycleDetector? _cycleDetector;
    private readonly TopologicalSort? _topoSort;
    private readonly IBulkCreator? _bulkCreator;
    private readonly ILogger<GenerationService> _logger;

    // Async-factory constructor fields (Task 3 path)
    private readonly Func<CancellationToken, Task<IOrganizationServiceAsync2>>? _serviceFactory;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Initializes a new instance of <see cref="GenerationService"/> with pre-resolved pipeline dependencies (DI path).</summary>
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

    /// <summary>Initializes a new instance of <see cref="GenerationService"/> with an async service factory (async-resolution path).</summary>
    public GenerationService(
        Func<CancellationToken, Task<IOrganizationServiceAsync2>> serviceFactory,
        ILoggerFactory loggerFactory)
    {
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<GenerationService>();
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

            var metadataList = await _metadata!.GetEntitiesAsync(config.EntityLogicalNames, ct);
            var metadataDict = metadataList.ToDictionary(e => e.LogicalName);

            uiProgress?.Report(new ProgressUpdate("Resolving dependencies", string.Empty, 0, 0, 0, 0, 0, TimeSpan.Zero));

            var graph = _graphBuilder!.Build(metadataDict);
            var cycles = _cycleDetector!.FindStronglyConnectedComponents(graph);
            if (cycles.Count > 0)
            {
                _logger.LogWarning("Breaking {Count} dependency cycle(s) before generation", cycles.Count);
                _cycleDetector.BreakCycles(graph, cycles, metadataDict);
            }

            uiProgress?.Report(new ProgressUpdate("Generating records", string.Empty, 0, 0, 0, 0, 0, TimeSpan.Zero));

            var progressRelayWindow = TimeSpan.FromMilliseconds(100);
            var lastProgressAt = TimeSpan.Zero;
            var bulkProgress = new Progress<BulkCreationProgress>(p =>
            {
                var now = sw.Elapsed;
                var isTerminal = p.BatchIndex == p.TotalBatches;
                if (!isTerminal && (now - lastProgressAt) < progressRelayWindow)
                    return;
                lastProgressAt = now;

                uiProgress?.Report(new ProgressUpdate(
                    Phase: "Generating",
                    EntityName: p.EntityLogicalName,
                    RecordsCreated: p.RecordsCreated,
                    TotalRecords: p.TotalRecords,
                    BatchesCompleted: p.BatchIndex,
                    TotalBatches: p.TotalBatches,
                    RecordsPerMinute: p.RecordsPerMinute,
                    Elapsed: now));
            });

            var result = await _bulkCreator!.CreateAsync(config, metadataDict, graph, bulkProgress, ct);

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

    /// <summary>
    /// Resolves the Dataverse service client via the async factory and runs a single-entity generation pass.
    /// This overload is used by the async-factory constructor path; full pipeline wiring is deferred to Task 4.
    /// </summary>
    public async Task<GenerationResult> GenerateAsync(
        string orgId,
        string entityLogicalName,
        int recordCount,
        CancellationToken ct)
    {
        if (_serviceFactory is null)
            throw new InvalidOperationException(
                "GenerateAsync(orgId, entityLogicalName, recordCount, ct) requires the async-factory constructor.");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var service = await _serviceFactory(ct).ConfigureAwait(false);

            // NOTE: Keep in sync with DI registration in Program.cs
            // Build per-run pipeline components using the resolved service.
            var lf = _loggerFactory!;
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var metadata = new DataverseMetadataProvider(service, cache, lf.CreateLogger<DataverseMetadataProvider>());
            var graphBuilder = new GraphBuilder(lf.CreateLogger<GraphBuilder>());
            var cycleDetector = new CycleDetector(lf.CreateLogger<CycleDetector>());
            var topoSort = new TopologicalSort(lf.CreateLogger<TopologicalSort>());
            var generatorFactory = new GeneratorFactory(lf.CreateLogger<GeneratorFactory>());
            var edgeCaseValidator = new EdgeCaseValidator(lf.CreateLogger<EdgeCaseValidator>());
            var messageChecker = new MessageAvailabilityChecker(service, lf.CreateLogger<MessageAvailabilityChecker>());
            var throttle = new ThrottlePolicy(lf.CreateLogger<ThrottlePolicy>());
            var deferred = new DeferredLookupBackfill(service, messageChecker, throttle, lf.CreateLogger<DeferredLookupBackfill>());
            var bulkCreator = new BulkCreator(service, generatorFactory, edgeCaseValidator, messageChecker, throttle, topoSort, deferred, lf.CreateLogger<BulkCreator>());

            _logger.LogInformation(
                "Starting generation for {OrgId}/{Entity} ({Count} records)",
                orgId, entityLogicalName, recordCount);

            var config = new GenerationConfig
            {
                EntityLogicalNames = [entityLogicalName],
                RecordCounts = new Dictionary<string, int> { [entityLogicalName] = recordCount }
            };

            var metadataList = await metadata.GetEntitiesAsync(config.EntityLogicalNames, ct).ConfigureAwait(false);
            var metadataDict = metadataList.ToDictionary(e => e.LogicalName);

            var graph = graphBuilder.Build(metadataDict);
            var cycles = cycleDetector.FindStronglyConnectedComponents(graph);
            if (cycles.Count > 0)
            {
                _logger.LogWarning("Breaking {Count} dependency cycle(s) before generation", cycles.Count);
                cycleDetector.BreakCycles(graph, cycles, metadataDict);
            }

            var result = await bulkCreator.CreateAsync(config, metadataDict, graph, null, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Generation complete — {Total} records in {Elapsed}",
                result.TotalRecords,
                sw.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Generation cancelled for {OrgId}/{Entity}", orgId, entityLogicalName);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation failed for {OrgId}/{Entity}", orgId, entityLogicalName);
            throw;
        }
    }
}

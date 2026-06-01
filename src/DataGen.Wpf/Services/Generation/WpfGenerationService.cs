using System.Diagnostics;
using DataGen.Bulk;
using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;
using DataGen.Desktop.Services.Dataverse;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DataGen.Desktop.Services.Generation;

/// <summary>
/// Runs the Core/Bulk generation pipeline from a WPF desktop context.
/// Builds a per-run pipeline inside <see cref="GenerateAsync"/> using a fresh
/// Dataverse service client obtained from <see cref="IDataverseConnectionService"/>.
/// Progress is dispatched to the UI thread via <see cref="System.Windows.Application.Current"/> dispatcher.
/// </summary>
public sealed class WpfGenerationService : IWpfGenerationService
{
    private static readonly TimeSpan ProgressGate = TimeSpan.FromMilliseconds(100);

    private readonly IDataverseConnectionService _conn;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WpfGenerationService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="conn">Dataverse connection service.</param>
    /// <param name="loggerFactory">Logger factory for per-run pipeline components.</param>
    public WpfGenerationService(IDataverseConnectionService conn, ILoggerFactory loggerFactory)
    {
        _conn = conn;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WpfGenerationService>();
    }

    /// <inheritdoc />
    public async Task<GenerationResult> GenerateAsync(
        GenerationConfig config,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            Report("Inspecting schema", string.Empty, 0, 0, 0, 0, 0);

            var service = await _conn.GetOrganizationServiceAsync(ct).ConfigureAwait(false);
            var lf = _loggerFactory;

            // Per-run pipeline — cache is scoped to this call and disposed on exit.
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
            var bulk = new BulkCreator(service, generatorFactory, edgeCaseValidator, messageChecker, throttle, topoSort, deferred, lf.CreateLogger<BulkCreator>());

            var metas = await metadata.GetEntitiesAsync(config.EntityLogicalNames, ct).ConfigureAwait(false);
            var metaDict = metas.ToDictionary(e => e.LogicalName);

            Report("Resolving dependencies", string.Empty, 0, 0, 0, 0, 0);

            var graph = graphBuilder.Build(metaDict);
            var cycles = cycleDetector.FindStronglyConnectedComponents(graph);
            if (cycles.Count > 0)
            {
                _logger.LogWarning("Breaking {Count} dependency cycle(s)", cycles.Count);
                cycleDetector.BreakCycles(graph, cycles, metaDict);
            }

            Report("Generating records", string.Empty, 0, 0, 0, 0, 0);

            // 100ms progress gate — always emit terminal updates.
            var lastAt = TimeSpan.Zero;
            var bulkProgress = new Progress<BulkCreationProgress>(p =>
            {
                var now = sw.Elapsed;
                var isTerminal = p.BatchIndex == p.TotalBatches;
                if (!isTerminal && now - lastAt < ProgressGate)
                    return;
                lastAt = now;
                var update = new ProgressUpdate("Generating", p.EntityLogicalName,
                    p.RecordsCreated, p.TotalRecords, p.BatchIndex, p.TotalBatches,
                    p.RecordsPerMinute, now);
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => progress.Report(update));
            });

            var result = await bulk.CreateAsync(config, metaDict, graph, bulkProgress, ct).ConfigureAwait(false);

            _logger.LogInformation("Generation complete — {Total} records in {Elapsed}",
                result.TotalRecords, sw.Elapsed);
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

        void Report(string phase, string entity, int created, int total, int batches, int totalBatches, double rpm) =>
            progress.Report(new ProgressUpdate(phase, entity, created, total, batches, totalBatches, rpm, sw.Elapsed));
    }
}

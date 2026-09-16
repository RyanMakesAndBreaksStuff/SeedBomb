using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;
using DataGen.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataGen.Bulk;

/// <summary>
/// Default Core/Bulk orchestration boundary for Dataverse generation runs.
/// </summary>
/// <remarks>Initializes a new instance of the <see cref="GenerationPipeline"/> class.</remarks>
/// <param name="metadata">Metadata provider reused across runs.</param>
/// <param name="loggerFactory">Logger factory used for pipeline components.</param>
public sealed class GenerationPipeline(IMetadataProvider metadata, ILoggerFactory loggerFactory)
{
    private readonly IMetadataProvider _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly ILogger<GenerationPipeline> _logger = loggerFactory.CreateLogger<GenerationPipeline>();

    /// <summary>
    /// Executes a full generation run for the selected entities: loads metadata, builds and
    /// topologically sorts the dependency graph, then bulk-creates records with deferred
    /// lookup backfill.
    /// </summary>
    /// <param name="config">Generation configuration.</param>
    /// <param name="service">Dataverse organization service used for write operations.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed generation result.</returns>
    public async Task<GenerationResult> GenerateAsync(
        GenerationConfig config,
        IOrganizationServiceAsync2 service,
        IProgress<GenerationPipelineProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(service);

        var lf = _loggerFactory;

        progress?.Report(new GenerationPipelineProgress { Phase = "Inspecting schema" });
        var metas = await _metadata.GetEntitiesAsync(config.EntityLogicalNames, ct).ConfigureAwait(false);
        var metaDict = metas.ToDictionary(e => e.LogicalName);

        progress?.Report(new GenerationPipelineProgress { Phase = "Resolving dependencies" });
        var graphBuilder = new GraphBuilder(lf.CreateLogger<GraphBuilder>());
        var cycleDetector = new CycleDetector(lf.CreateLogger<CycleDetector>());
        // Backfill ownership (LookupRulePolicy.IsExplicit, used in BulkCreator) is a different
        // question: lookupRandom owns its written value but still depends on its in-run targets.
        bool SuppliesValueWithoutDependency(string table, string column) =>
            LookupRulePolicy.SuppliesValueWithoutDependency(config.FieldRules, table, column);
        var graph = graphBuilder.Build(metaDict, SuppliesValueWithoutDependency);
        var cycles = cycleDetector.FindStronglyConnectedComponents(graph);
        if (cycles.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("Breaking {Count} dependency cycle(s)", cycles.Count);
            }
            cycleDetector.BreakCycles(graph, cycles, metaDict, SuppliesValueWithoutDependency);
        }

        progress?.Report(new GenerationPipelineProgress { Phase = "Generating records" });
        var topoSort = new TopologicalSort(lf.CreateLogger<TopologicalSort>());
        var generatorFactory = new GeneratorFactory(lf.CreateLogger<GeneratorFactory>());
        var edgeCaseValidator = new EdgeCaseValidator(lf.CreateLogger<EdgeCaseValidator>());
        var messageChecker = new MessageAvailabilityChecker(service, lf.CreateLogger<MessageAvailabilityChecker>());
        var throttle = new ThrottlePolicy(lf.CreateLogger<ThrottlePolicy>());
        var deferred = new DeferredLookupBackfill(service, messageChecker, throttle, lf.CreateLogger<DeferredLookupBackfill>());
        var bulk = new BulkCreator(
            service,
            generatorFactory,
            edgeCaseValidator,
            messageChecker,
            throttle,
            topoSort,
            deferred,
            lf.CreateLogger<BulkCreator>());

        var bulkProgress = progress is null ? null : new Progress<BulkCreationProgress>(p =>
            progress.Report(new GenerationPipelineProgress
            {
                Phase = p.Phase,
                EntityLogicalName = p.EntityLogicalName,
                RecordsCreated = p.RecordsCreated,
                TotalRecords = p.TotalRecords,
                BatchIndex = p.BatchIndex,
                TotalBatches = p.TotalBatches,
                RecordsPerMinute = p.RecordsPerMinute,
                ErrorMessage = p.ErrorMessage
            }));

        return await bulk.CreateAsync(config, metaDict, graph, bulkProgress, ct).ConfigureAwait(false);
    }
}

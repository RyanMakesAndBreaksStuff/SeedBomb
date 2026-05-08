using DataGen.Core.Contracts;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace DataGen.Bulk;

/// <summary>
/// Backfills deferred lookup fields and N:N associations after the initial record creation pass.
/// Deferred lookups arise when a cycle is broken by removing an optional lookup edge.
/// </summary>
public class DeferredLookupBackfill
{
    private readonly IOrganizationServiceAsync2 _service;
    private readonly ThrottlePolicy _throttlePolicy;
    private readonly ILogger<DeferredLookupBackfill> _logger;
    private const int MaxRetries = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeferredLookupBackfill"/> class.
    /// </summary>
    /// <param name="service">The Dataverse organization service.</param>
    /// <param name="throttlePolicy">The throttle retry policy.</param>
    /// <param name="logger">The logger instance.</param>
    public DeferredLookupBackfill(
        IOrganizationServiceAsync2 service,
        ThrottlePolicy throttlePolicy,
        ILogger<DeferredLookupBackfill> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _throttlePolicy = throttlePolicy ?? throw new ArgumentNullException(nameof(throttlePolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends UpdateRequest batches to populate deferred lookup fields.
    /// Each source entity record gets its deferred lookups set to a random target record.
    /// </summary>
    /// <param name="graph">The dependency graph with deferred edge information.</param>
    /// <param name="pool">The record pool containing IDs of all created records.</param>
    /// <param name="batchSize">Number of updates per ExecuteMultiple batch.</param>
    /// <param name="seed">RNG seed for deterministic target selection. Should match the generation seed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Any batch errors encountered.</returns>
    public async Task<IReadOnlyList<BatchError>> BackfillLookupsAsync(
        DependencyGraph graph,
        DataverseRecordPool pool,
        int batchSize,
        int seed = 42,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(pool);

        var errors = new List<BatchError>();

        if (graph.DeferredEdges.Count == 0)
        {
            _logger.LogDebug("No deferred lookups to backfill.");
            return errors.AsReadOnly();
        }

        _logger.LogInformation("Starting deferred lookup backfill for {EntityCount} entities.", graph.DeferredEdges.Count);

        var rng = new Random(seed);

        foreach (var (sourceEntity, deferredLookups) in graph.DeferredEdges)
        {
            var sourceIds = pool.Get(sourceEntity);
            if (sourceIds.Count == 0)
            {
                _logger.LogWarning("No records in pool for {Entity}; skipping deferred backfill.", sourceEntity);
                continue;
            }

            _logger.LogInformation(
                "Backfilling {FieldCount} deferred lookup(s) for {Entity} ({RecordCount} records).",
                deferredLookups.Count, sourceEntity, sourceIds.Count);

            // Build update entities for all source records
            var updates = sourceIds.Select(id =>
            {
                var update = new Entity(sourceEntity) { Id = id };
                foreach (var deferred in deferredLookups)
                {
                    // Pick a random target from the first available target entity that has records
                    foreach (var targetEntity in deferred.TargetEntities)
                    {
                        var targetIds = pool.Get(targetEntity);
                        if (targetIds.Count > 0)
                        {
                            var targetId = targetIds[rng.Next(targetIds.Count)];
                            update[deferred.FieldLogicalName] = new EntityReference(targetEntity, targetId);
                            break;
                        }
                    }
                }
                return update;
            }).ToList();

            // Submit in batches via ExecuteMultiple
            var batchErrors = await SubmitUpdateBatchesAsync(sourceEntity, updates, batchSize, ct).ConfigureAwait(false);
            errors.AddRange(batchErrors);
        }

        _logger.LogInformation("Deferred lookup backfill complete. Errors: {ErrorCount}", errors.Count);
        return errors.AsReadOnly();
    }

    /// <summary>
    /// Creates N:N associations for all many-to-many relationships using AssociateRequest batches.
    /// Each Entity1 record is associated with a random Entity2 record.
    /// </summary>
    /// <param name="graph">The dependency graph with N:N relationship information.</param>
    /// <param name="pool">The record pool containing IDs of all created records.</param>
    /// <param name="batchSize">Number of associations per ExecuteMultiple batch.</param>
    /// <param name="seed">RNG seed for deterministic association selection. Should match the generation seed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Any batch errors encountered.</returns>
    public async Task<IReadOnlyList<BatchError>> AssociateManyToManyAsync(
        DependencyGraph graph,
        DataverseRecordPool pool,
        int batchSize,
        int seed = 42,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(pool);

        var errors = new List<BatchError>();
        var processedRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("Starting N:N association pass.");
        var rng = new Random(seed);

        foreach (var (entityName, relationships) in graph.Relationships)
        {
            foreach (var rel in relationships)
            {
                // Process each relationship only once (it appears under both entities)
                if (!processedRelationships.Add(rel.SchemaName))
                    continue;

                var entity1Ids = pool.Get(rel.Entity1LogicalName);
                var entity2Ids = pool.Get(rel.Entity2LogicalName);

                if (entity1Ids.Count == 0 || entity2Ids.Count == 0)
                {
                    _logger.LogWarning(
                        "Skipping N:N association {Schema}: no records for {E1} or {E2}.",
                        rel.SchemaName, rel.Entity1LogicalName, rel.Entity2LogicalName);
                    continue;
                }

                _logger.LogInformation(
                    "Creating N:N associations for {Schema} ({E1} ↔ {E2}).",
                    rel.SchemaName, rel.Entity1LogicalName, rel.Entity2LogicalName);

                // Associate each Entity1 record with one random Entity2 record
                var requests = entity1Ids.Select(e1Id =>
                {
                    var e2Id = entity2Ids[rng.Next(entity2Ids.Count)];
                    return (OrganizationRequest)new AssociateRequest
                    {
                        Target = new EntityReference(rel.Entity1LogicalName, e1Id),
                        Relationship = new Relationship(rel.SchemaName),
                        RelatedEntities = new EntityReferenceCollection
                        {
                            new EntityReference(rel.Entity2LogicalName, e2Id)
                        }
                    };
                }).ToList();

                var batchErrors = await SubmitOrganizationRequestBatchesAsync(
                    $"{rel.SchemaName} (N:N)", requests, batchSize, ct).ConfigureAwait(false);
                errors.AddRange(batchErrors);
            }
        }

        _logger.LogInformation("N:N association pass complete. Errors: {ErrorCount}", errors.Count);
        return errors.AsReadOnly();
    }

    private async Task<List<BatchError>> SubmitUpdateBatchesAsync(
        string entityName,
        List<Entity> updates,
        int batchSize,
        CancellationToken ct)
    {
        var requests = updates.Select(e => (OrganizationRequest)new UpdateRequest { Target = e }).ToList();
        return await SubmitOrganizationRequestBatchesAsync(entityName, requests, batchSize, ct).ConfigureAwait(false);
    }

    private async Task<List<BatchError>> SubmitOrganizationRequestBatchesAsync(
        string context,
        List<OrganizationRequest> requests,
        int batchSize,
        CancellationToken ct)
    {
        var errors = new List<BatchError>();
        var batches = requests.Chunk(batchSize).ToList();

        for (int i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var batch = batches[i];
            var execMulti = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings
                {
                    ContinueOnError = true,
                    ReturnResponses = false
                },
                Requests = new OrganizationRequestCollection()
            };
            foreach (var req in batch)
                execMulti.Requests.Add(req);

            try
            {
                var response = await _throttlePolicy.ExecuteAsync(
                    async () => (ExecuteMultipleResponse)await _service
                        .ExecuteAsync(execMulti, ct).ConfigureAwait(false),
                    context, MaxRetries, ct).ConfigureAwait(false);

                if (response.IsFaulted)
                {
                    var faultedCount = response.Responses.Count(r => r.Fault is not null);
                    _logger.LogWarning(
                        "{Context}: batch {BatchIndex}/{Total} had {FaultCount} fault(s).",
                        context, i + 1, batches.Count, faultedCount);

                    foreach (var item in response.Responses.Where(r => r.Fault is not null))
                    {
                        errors.Add(new BatchError(context, i, item.Fault?.Message ?? "Unknown fault", item.Fault?.ErrorCode));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Context}: batch {BatchIndex}/{Total} failed.", context, i + 1, batches.Count);
                errors.Add(new BatchError(context, i, ex.Message, null));
            }
        }

        return errors;
    }
}

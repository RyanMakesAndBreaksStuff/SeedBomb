using SeedBomb.Bulk.Contracts;
using SeedBomb.Core.Contracts;
using SeedBomb.Core.Generators;
using SeedBomb.Core.Graph;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace SeedBomb.Bulk;

/// <summary>
/// Backfills deferred lookup fields and N:N associations after the initial record creation pass.
/// Deferred lookups arise when a cycle is broken by removing an optional lookup edge.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DeferredLookupBackfill"/> class.
/// </remarks>
/// <param name="service">The Dataverse organization service.</param>
/// <param name="messageAvailabilityChecker">Message capability checker.</param>
/// <param name="throttlePolicy">The throttle retry policy.</param>
/// <param name="logger">The logger instance.</param>
public class DeferredLookupBackfill(
    IOrganizationServiceAsync2 service,
    MessageAvailabilityChecker messageAvailabilityChecker,
    ThrottlePolicy throttlePolicy,
    ILogger<DeferredLookupBackfill> logger)
{
    private readonly IOrganizationServiceAsync2 _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly MessageAvailabilityChecker _messageAvailabilityChecker = messageAvailabilityChecker ?? throw new ArgumentNullException(nameof(messageAvailabilityChecker));
    private readonly ThrottlePolicy _throttlePolicy = throttlePolicy ?? throw new ArgumentNullException(nameof(throttlePolicy));
    private readonly ILogger<DeferredLookupBackfill> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Sends UpdateRequest batches to populate deferred lookup fields.
    /// Each source entity record gets its deferred lookups set to a random target record.
    /// </summary>
    /// <param name="graph">The dependency graph with deferred edge information.</param>
    /// <param name="pool">The record pool containing IDs of all created records.</param>
    /// <param name="batchSize">Number of updates per ExecuteMultiple batch.</param>
    /// <param name="seed">RNG seed for deterministic target selection. Should match the generation seed.</param>
    /// <param name="maxRetries">Maximum number of retry attempts for throttle faults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="isExplicitLookup">
    /// Optional predicate identifying lookup columns that supply their own value or omission
    /// and must not be overwritten during backfill. Arguments are source table and column logical names.
    /// </param>
    /// <param name="progress">
    /// Optional progress reporter. Each deferred-edge source entity emits one "Linking" snapshot so the
    /// UI can show that the run is still working after record creation finishes.
    /// </param>
    /// <returns>Any batch errors encountered.</returns>
    public async Task<IReadOnlyList<BatchError>> BackfillLookupsAsync(
        DependencyGraph graph,
        DataverseRecordPool pool,
        int batchSize,
        int seed = 42,
        int maxRetries = 3,
        CancellationToken ct = default,
        Func<string, string, bool>? isExplicitLookup = null,
        IProgress<BulkCreationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(pool);

        var errors = new List<BatchError>();

        if (graph.DeferredEdges.Count == 0)
        {
            _logger.LogDebug("No deferred lookups to backfill.");
            return errors.AsReadOnly();
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Starting deferred lookup backfill for {EntityCount} entities.", graph.DeferredEdges.Count);
        }

        var rng = new Random(seed);

        // One unit of work is one source entity. Report at the top of the loop body so every
        // iteration emits a snapshot, including the ones that skip via continue below.
        var totalUnits = graph.DeferredEdges.Count;
        var unit = 0;

        foreach (var (sourceEntity, deferredLookups) in graph.DeferredEdges)
        {
            unit++;
            progress?.Report(new BulkCreationProgress
            {
                Phase = "Linking",
                EntityLogicalName = sourceEntity,
                BatchIndex = unit,
                TotalBatches = totalUnits,
                RecordsCreated = unit,
                TotalRecords = totalUnits,
            });

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
                    // Pick a random target from the first available target entity that has records.
                    // Sort by logical name for deterministic selection across schema refreshes.
                    foreach (var targetEntity in deferred.TargetEntities.Order())
                    {
                        var targetIds = pool.Get(targetEntity);
                        if (targetIds.Count > 0)
                        {
                            var targetId = targetIds[rng.Next(targetIds.Count)];
                            if (isExplicitLookup?.Invoke(sourceEntity, deferred.FieldLogicalName) != true)
                                update[deferred.FieldLogicalName] = new EntityReference(targetEntity, targetId);
                            break;
                        }
                    }
                }
                return update;
            }).Where(u => u.Attributes.Count > 0).ToList();

            if (updates.Count == 0)
                continue;

            var useUpdateMultiple = await _messageAvailabilityChecker.IsUpdateMultipleAvailableAsync(sourceEntity, ct).ConfigureAwait(false);
            var batchErrors = useUpdateMultiple
                ? await SubmitUpdateMultipleBatchesAsync(sourceEntity, updates, batchSize, maxRetries, ct).ConfigureAwait(false)
                : await SubmitUpdateBatchesAsync(sourceEntity, updates, batchSize, maxRetries, ct).ConfigureAwait(false);
            errors.AddRange(batchErrors);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Deferred lookup backfill complete. Errors: {ErrorCount}", errors.Count);
        }
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
    /// <param name="maxRetries">Maximum number of retry attempts for throttle faults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">
    /// Optional progress reporter. Each distinct relationship emits one "Linking" snapshot so the
    /// UI can show that the run is still working after record creation finishes.
    /// </param>
    /// <returns>Any batch errors encountered.</returns>
    public async Task<IReadOnlyList<BatchError>> AssociateManyToManyAsync(
        DependencyGraph graph,
        DataverseRecordPool pool,
        int batchSize,
        int seed = 42,
        int maxRetries = 3,
        CancellationToken ct = default,
        IProgress<BulkCreationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(pool);

        var errors = new List<BatchError>();
        var processedRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("Starting N:N association pass.");
        var rng = new Random(seed);

        // One unit of work is one distinct relationship; total is cheap to precompute because
        // relationships appear under both entities and must be de-duplicated the same way.
        var totalUnits = graph.Relationships
            .SelectMany(kv => kv.Value)
            .Select(r => r.SchemaName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var unit = 0;

        foreach (var (entityName, relationships) in graph.Relationships)
        {
            foreach (var rel in relationships)
            {
                // Process each relationship only once (it appears under both entities)
                if (!processedRelationships.Add(rel.SchemaName))
                    continue;

                unit++;
                progress?.Report(new BulkCreationProgress
                {
                    Phase = "Linking",
                    EntityLogicalName = rel.Entity1LogicalName,
                    BatchIndex = unit,
                    TotalBatches = totalUnits,
                    RecordsCreated = unit,
                    TotalRecords = totalUnits,
                });

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
                        RelatedEntities =
                        [
                            new EntityReference(rel.Entity2LogicalName, e2Id)
                        ]
                    };
                }).ToList();

                var batchErrors = await SubmitOrganizationRequestBatchesAsync(
                    $"{rel.SchemaName} (N:N)", requests, batchSize, maxRetries, ct).ConfigureAwait(false);
                errors.AddRange(batchErrors);
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("N:N association pass complete. Errors: {ErrorCount}", errors.Count);
        }
        return errors.AsReadOnly();
    }

    private async Task<List<BatchError>> SubmitUpdateBatchesAsync(
        string entityName,
        List<Entity> updates,
        int batchSize,
        int maxRetries,
        CancellationToken ct)
    {
        var requests = updates.Select(e => (OrganizationRequest)new UpdateRequest { Target = e }).ToList();
        return await SubmitOrganizationRequestBatchesAsync(entityName, requests, batchSize, maxRetries, ct).ConfigureAwait(false);
    }

    private async Task<List<BatchError>> SubmitUpdateMultipleBatchesAsync(
        string entityName,
        List<Entity> updates,
        int batchSize,
        int maxRetries,
        CancellationToken ct)
    {
        var errors = new List<BatchError>();
        var batches = updates.Chunk(batchSize).ToList();

        for (int i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var request = new UpdateMultipleRequest
            {
                Targets = new EntityCollection(batches[i]) { EntityName = entityName }
            };

            try
            {
                await _throttlePolicy.ExecuteAsync(
                    async () => await _service.ExecuteAsync(request, ct).ConfigureAwait(false),
                    entityName,
                    maxRetries,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "UpdateMultiple failed for {Entity}; falling back to ExecuteMultiple updates.", entityName);
                // WR-011: batches 0..i-1 already committed — re-send only the ones UpdateMultiple did not.
                return await SubmitUpdateBatchesAsync(
                    entityName, batches.Skip(i).SelectMany(b => b).ToList(), batchSize, maxRetries, ct).ConfigureAwait(false);
            }
        }

        return errors;
    }

    private async Task<List<BatchError>> SubmitOrganizationRequestBatchesAsync(
        string context,
        List<OrganizationRequest> requests,
        int batchSize,
        int maxRetries,
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
                Requests = []
            };
            foreach (var req in batch)
                execMulti.Requests.Add(req);

            try
            {
                var response = await _throttlePolicy.ExecuteAsync(
                    async () => (ExecuteMultipleResponse)await _service
                        .ExecuteAsync(execMulti, ct).ConfigureAwait(false),
                    context, maxRetries, ct).ConfigureAwait(false);

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
                errors.Add(new BatchError(context, i, ex.Message, null, batch.Length));
            }
        }

        return errors;
    }
}

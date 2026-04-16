using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Bulk;

/// <summary>
/// Generates and bulk-creates Dataverse records in topological dependency order.
/// Uses <c>CreateMultiple</c> where available and falls back to <c>ExecuteMultiple</c>.
/// Bogus record generation is sequential per entity; API calls are parallelised per batch.
/// </summary>
public class BulkCreator : IBulkCreator
{
    private readonly IOrganizationServiceAsync2 _service;
    private readonly GeneratorFactory _generatorFactory;
    private readonly EdgeCaseValidator _edgeCaseValidator;
    private readonly MessageAvailabilityChecker _messageChecker;
    private readonly ThrottlePolicy _throttlePolicy;
    private readonly TopologicalSort _topologicalSort;
    private readonly DeferredLookupBackfill _deferredBackfill;
    private readonly ILogger<BulkCreator> _logger;

    private static bool _threadPoolTuned;
    private static readonly object _tuningLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkCreator"/> class.
    /// </summary>
    public BulkCreator(
        IOrganizationServiceAsync2 service,
        GeneratorFactory generatorFactory,
        EdgeCaseValidator edgeCaseValidator,
        MessageAvailabilityChecker messageChecker,
        ThrottlePolicy throttlePolicy,
        TopologicalSort topologicalSort,
        DeferredLookupBackfill deferredBackfill,
        ILogger<BulkCreator> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _generatorFactory = generatorFactory ?? throw new ArgumentNullException(nameof(generatorFactory));
        _edgeCaseValidator = edgeCaseValidator ?? throw new ArgumentNullException(nameof(edgeCaseValidator));
        _messageChecker = messageChecker ?? throw new ArgumentNullException(nameof(messageChecker));
        _throttlePolicy = throttlePolicy ?? throw new ArgumentNullException(nameof(throttlePolicy));
        _topologicalSort = topologicalSort ?? throw new ArgumentNullException(nameof(topologicalSort));
        _deferredBackfill = deferredBackfill ?? throw new ArgumentNullException(nameof(deferredBackfill));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<GenerationResult> CreateAsync(
        GenerationConfig config,
        IReadOnlyDictionary<string, EntityMetadata> entityMetadata,
        DependencyGraph graph,
        IProgress<BulkCreationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(entityMetadata);
        ArgumentNullException.ThrowIfNull(graph);

        EnsureThreadPoolTuned();

        var sortedEntities = _topologicalSort.Sort(graph);
        var pool = new DataverseRecordPool();
        var allCreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.OrdinalIgnoreCase);
        var allErrors = new List<BatchError>();
        var runStart = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Starting bulk creation for {EntityCount} entities.",
            sortedEntities.Count);

        for (int entityIndex = 0; entityIndex < sortedEntities.Count; entityIndex++)
        {
            var entityName = sortedEntities[entityIndex];
            ct.ThrowIfCancellationRequested();

            if (!config.RecordCounts.TryGetValue(entityName, out var recordCount) || recordCount <= 0)
            {
                _logger.LogDebug("Skipping {Entity}: no record count specified.", entityName);
                continue;
            }

            if (!entityMetadata.TryGetValue(entityName, out var meta))
            {
                _logger.LogWarning("Skipping {Entity}: metadata not found.", entityName);
                continue;
            }

            _logger.LogInformation(
                "Generating {RecordCount} records for {Entity}.",
                recordCount, entityName);

            var (createdIds, errors) = await CreateEntityRecordsAsync(
                entityName, meta, entityIndex, recordCount, config, pool, progress, ct).ConfigureAwait(false);

            pool.Add(entityName, createdIds);
            allCreatedRecords[entityName] = createdIds.AsReadOnly();
            allErrors.AddRange(errors);

            _logger.LogInformation(
                "Completed {Entity}: {Created}/{Requested} records created.",
                entityName, createdIds.Count, recordCount);
        }

        // Phase 2: backfill deferred lookups
        _logger.LogInformation("Starting Phase 2: deferred lookup backfill.");
        var backfillErrors = await _deferredBackfill.BackfillLookupsAsync(
            graph, pool, config.BatchSize, ct).ConfigureAwait(false);
        allErrors.AddRange(backfillErrors);

        // Phase 3: N:N associations
        var associateErrors = await _deferredBackfill.AssociateManyToManyAsync(
            graph, pool, config.BatchSize, ct).ConfigureAwait(false);
        allErrors.AddRange(associateErrors);

        var elapsed = DateTimeOffset.UtcNow - runStart;
        _logger.LogInformation(
            "Bulk creation complete. Total records: {Total}. Errors: {Errors}. Elapsed: {Elapsed}.",
            allCreatedRecords.Values.Sum(v => v.Count), allErrors.Count, elapsed);

        return new GenerationResult
        {
            CreatedRecords = allCreatedRecords,
            Errors = allErrors.AsReadOnly(),
            Elapsed = elapsed
        };
    }

    private async Task<(List<Guid> ids, List<BatchError> errors)> CreateEntityRecordsAsync(
        string entityName,
        EntityMetadata meta,
        int entityIndex,
        int recordCount,
        GenerationConfig config,
        DataverseRecordPool pool,
        IProgress<BulkCreationProgress>? progress,
        CancellationToken ct)
    {
        // Determine which attributes to generate (sequential, on calling thread)
        var attributesToGenerate = GetGeneratableAttributes(meta);

        // Sequential Bogus generation — single Faker instance, no sharing across threads
        var faker = DeterministicFaker.Create(config.Seed, entityIndex);
        var entities = new List<Entity>(recordCount);

        for (int i = 0; i < recordCount; i++)
        {
            var entity = new Entity(entityName);
            foreach (var attr in attributesToGenerate)
            {
                var value = _generatorFactory.Generate(attr, faker, pool);
                if (value is not null)
                    entity[attr.LogicalName!] = value;
            }
            entities.Add(entity);
        }

        // Split into batches and submit in parallel
        var batches = entities.Chunk(config.BatchSize).ToArray();
        var useCreateMultiple = await _messageChecker
            .IsCreateMultipleAvailableAsync(entityName, ct).ConfigureAwait(false);

        var allIds = new List<Guid>(recordCount);
        var allErrors = new List<BatchError>();
        var createdCount = 0;
        var entityStart = DateTimeOffset.UtcNow;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = config.MaxParallelism ?? Environment.ProcessorCount,
            CancellationToken = ct
        };

        // Thread-safe accumulators for parallel batch results
        var idBags = new List<Guid>[batches.Length];
        var errorBags = new List<BatchError>[batches.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, batches.Length),
            parallelOptions,
            async (batchIndex, innerCt) =>
            {
                var batch = batches[batchIndex];
                var (batchIds, batchErrors) = await SubmitBatchAsync(
                    entityName, batch, useCreateMultiple, config.MaxRetries, innerCt).ConfigureAwait(false);

                idBags[batchIndex] = batchIds;
                errorBags[batchIndex] = batchErrors;

                var elapsed = (DateTimeOffset.UtcNow - entityStart).TotalMinutes;
                var ratePerMin = elapsed > 0
                    ? (Interlocked.Add(ref createdCount, batchIds.Count)) / elapsed
                    : 0;

                progress?.Report(new BulkCreationProgress
                {
                    EntityLogicalName = entityName,
                    BatchIndex = batchIndex + 1,
                    TotalBatches = batches.Length,
                    RecordsCreated = createdCount,
                    TotalRecords = recordCount,
                    RecordsPerMinute = ratePerMin,
                    ErrorMessage = batchErrors.Count > 0 ? batchErrors[0].ErrorMessage : null
                });
            }).ConfigureAwait(false);

        foreach (var idBag in idBags)
            if (idBag is not null) allIds.AddRange(idBag);
        foreach (var errorBag in errorBags)
            if (errorBag is not null) allErrors.AddRange(errorBag);

        return (allIds, allErrors);
    }

    private async Task<(List<Guid> ids, List<BatchError> errors)> SubmitBatchAsync(
        string entityName,
        Entity[] batch,
        bool useCreateMultiple,
        int maxRetries,
        CancellationToken ct)
    {
        if (useCreateMultiple)
        {
            return await _throttlePolicy.ExecuteAsync(
                () => CreateMultipleAsync(entityName, batch, ct),
                entityName, maxRetries, ct).ConfigureAwait(false);
        }
        else
        {
            return await _throttlePolicy.ExecuteAsync(
                () => ExecuteMultipleFallbackAsync(entityName, batch, ct),
                entityName, maxRetries, ct).ConfigureAwait(false);
        }
    }

    private async Task<(List<Guid> ids, List<BatchError> errors)> CreateMultipleAsync(
        string entityName,
        Entity[] batch,
        CancellationToken ct)
    {
        var request = new CreateMultipleRequest
        {
            Targets = new EntityCollection(batch.ToList())
        };

        var response = (CreateMultipleResponse)await _service.ExecuteAsync(request, ct).ConfigureAwait(false);
        var ids = response.Ids?.ToList() ?? [];

        _logger.LogDebug("CreateMultiple: {Entity} batch of {BatchSize} → {IdCount} IDs.",
            entityName, batch.Length, ids.Count);

        return (ids, []);
    }

    private async Task<(List<Guid> ids, List<BatchError> errors)> ExecuteMultipleFallbackAsync(
        string entityName,
        Entity[] batch,
        CancellationToken ct)
    {
        var requests = new OrganizationRequestCollection();
        foreach (var entity in batch)
            requests.Add(new CreateRequest { Target = entity });

        var execMulti = new ExecuteMultipleRequest
        {
            Settings = new ExecuteMultipleSettings
            {
                ContinueOnError = true,
                ReturnResponses = true
            },
            Requests = requests
        };

        var response = (ExecuteMultipleResponse)await _service.ExecuteAsync(execMulti, ct).ConfigureAwait(false);

        var ids = new List<Guid>();
        var errors = new List<BatchError>();

        foreach (var item in response.Responses)
        {
            if (item.Fault is not null)
            {
                errors.Add(new BatchError(entityName, item.RequestIndex, item.Fault.Message, item.Fault.ErrorCode));
            }
            else if (item.Response is CreateResponse createResp)
            {
                ids.Add(createResp.id);
            }
        }

        _logger.LogDebug("ExecuteMultiple: {Entity} batch of {BatchSize} → {IdCount} IDs, {ErrorCount} errors.",
            entityName, batch.Length, ids.Count, errors.Count);

        return (ids, errors);
    }

    private AttributeMetadata[] GetGeneratableAttributes(EntityMetadata meta)
    {
        if (meta.Attributes is null) return [];

        return meta.Attributes
            .Where(FieldFilter.ShouldGenerateField)
            .Where(a => _edgeCaseValidator.Validate(a, meta).Action != FieldAction.Fail)
            .ToArray();
    }

    private static void EnsureThreadPoolTuned()
    {
        if (_threadPoolTuned) return;
        lock (_tuningLock)
        {
            if (_threadPoolTuned) return;
            // Boost minimum threads to reduce latency ramp-up for burst parallel API calls
            ThreadPool.SetMinThreads(100, 100);
            _threadPoolTuned = true;
        }
    }
}

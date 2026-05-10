using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using DataGen.Core.Exceptions;
using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System.ServiceModel;

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

    private const int MinThreadPoolThreads = 100;

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

        var pool = new DataverseRecordPool();
        await PopulateCurrencyPoolAsync(pool, ct).ConfigureAwait(false);
        EnsureThreadPoolTuned();

        var sortedEntities = _topologicalSort.Sort(graph);
        var allCreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.OrdinalIgnoreCase);
        var allErrors = new List<BatchError>();
        var runStart = DateTimeOffset.UtcNow;

        // T-18: default DOP=8; updated after first entity if ServiceClient provides a recommendation
        var effectiveDop = config.MaxParallelism ?? 8;

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
                entityName, meta, entityIndex, recordCount, config, effectiveDop, pool, progress, ct).ConfigureAwait(false);

            pool.Add(entityName, createdIds);
            allCreatedRecords[entityName] = createdIds.AsReadOnly();
            allErrors.AddRange(errors);

            _logger.LogInformation(
                "Completed {Entity}: {Created}/{Requested} records created.",
                entityName, createdIds.Count, recordCount);

            // After first entity, read server-recommended DOP if no user override
            if (entityIndex == 0 && config.MaxParallelism is null &&
                _service is ServiceClient sc)
            {
                var hint = sc.RecommendedDegreesOfParallelism;
                if (hint > 0)
                {
                    effectiveDop = hint;
                    _logger.LogInformation(
                        "DOP updated to {Dop} from ServiceClient.RecommendedDegreesOfParallelism",
                        effectiveDop);
                }
            }
        }

        // Phase 2: backfill deferred lookups
        _logger.LogInformation("Starting Phase 2: deferred lookup backfill.");
        var backfillErrors = await _deferredBackfill.BackfillLookupsAsync(
            graph, pool, config.BatchSize, config.Seed, maxRetries: config.MaxRetries, ct: ct).ConfigureAwait(false);
        allErrors.AddRange(backfillErrors);

        // Phase 3: N:N associations
        var associateErrors = await _deferredBackfill.AssociateManyToManyAsync(
            graph, pool, config.BatchSize, config.Seed, maxRetries: config.MaxRetries, ct: ct).ConfigureAwait(false);
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
        int effectiveDop,
        DataverseRecordPool pool,
        IProgress<BulkCreationProgress>? progress,
        CancellationToken ct)
    {
        // T-06: pre-flight — warn about any attributes that will be silently skipped due to FieldAction.Fail
        if (meta.Attributes is not null)
        {
            foreach (var attr in meta.Attributes)
            {
                var validation = _edgeCaseValidator.Validate(attr, meta);
                if (validation.Action == FieldAction.Fail)
                    _logger.LogWarning(
                        "Entity {Entity}: field {Field} has FieldAction.Fail — field will be skipped",
                        entityName, attr.LogicalName);
            }
        }

        // T-25: pre-flight required-lookup validation
        if (meta.Attributes is not null)
        {
            foreach (var attr in meta.Attributes.OfType<LookupAttributeMetadata>())
            {
                var level = attr.RequiredLevel?.Value ?? AttributeRequiredLevel.None;
                if (level == AttributeRequiredLevel.SystemRequired)
                    throw new DataGenerationException(
                        $"Entity '{entityName}': required lookup '{attr.LogicalName}' (SystemRequired) has no generator — cannot create records.");
                if (level == AttributeRequiredLevel.ApplicationRequired)
                    _logger.LogWarning(
                        "Entity {Entity}: lookup {Field} is ApplicationRequired but may have no generator",
                        entityName, attr.LogicalName);
            }
        }

        // Sequential Bogus generation — single Faker instance, no sharing across threads
        var attributesToGenerate = GetGeneratableAttributes(meta);
        var alternateKeyAttrs = GetAlternateKeyAttributes(meta);
        var specialHandlingAttrs = GetRoutableSpecialHandlingAttributes(meta);
        var hasMoney = specialHandlingAttrs.Any(a => a is MoneyAttributeMetadata);
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

            // Special handling attrs that route to generators (DateTime, MultiSelect, PolymorphicLookup, OwnerLookup, RichText, Money)
            foreach (var attr in specialHandlingAttrs)
            {
                var value = _generatorFactory.Generate(attr, faker, pool);
                if (value is not null)
                    entity[attr.LogicalName!] = value;
            }

            // Inject transactioncurrencyid for entities with money fields
            if (hasMoney)
            {
                var currencyId = pool.GetRandom("transactioncurrency", faker);
                if (currencyId.HasValue)
                    entity["transactioncurrencyid"] = new EntityReference("transactioncurrency", currencyId.Value);
                else
                    _logger.LogWarning("No transactioncurrency in pool for {Entity} — money fields may be rejected by Dataverse", entityName);
            }

            foreach (var attr in alternateKeyAttrs)
            {
                entity[attr.LogicalName!] = GenerateUniqueKeyValue(attr, entityName, i);
            }
            entities.Add(entity);
        }

        var batches = entities.Chunk(config.BatchSize).ToArray();
        var useCreateMultiple = await _messageChecker
            .IsCreateMultipleAvailableAsync(entityName, ct).ConfigureAwait(false);

        return await SubmitEntityBatchesAsync(
            entityName, batches, useCreateMultiple, recordCount, config, effectiveDop, progress, ct).ConfigureAwait(false);
    }

    private async Task<(List<Guid> ids, List<BatchError> errors)> SubmitEntityBatchesAsync(
        string entityName,
        Entity[][] batches,
        bool useCreateMultiple,
        int recordCount,
        GenerationConfig config,
        int effectiveDop,
        IProgress<BulkCreationProgress>? progress,
        CancellationToken ct)
    {
        var allIds = new List<Guid>(recordCount);
        var allErrors = new List<BatchError>();
        var createdCount = 0;
        var entityStart = DateTimeOffset.UtcNow;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = effectiveDop,
            CancellationToken = ct
        };

        // Thread-safe accumulators indexed by batch position
        var idBags = new List<Guid>[batches.Length];
        var errorBags = new List<BatchError>[batches.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, batches.Length),
            parallelOptions,
            async (batchIndex, innerCt) =>
            {
                var batch = batches[batchIndex];
                List<Guid> batchIds;
                List<BatchError> batchErrors;

                try
                {
                    (batchIds, batchErrors) = await SubmitBatchAsync(
                        entityName, batch, useCreateMultiple, config.MaxRetries, innerCt).ConfigureAwait(false);
                }
                catch (DataGenerationException ex)
                {
                    _logger.LogError(ex, "Batch {BatchIndex}/{TotalBatches} for {Entity} failed",
                        batchIndex + 1, batches.Length, entityName);
                    batchIds = [];
                    batchErrors = [new BatchError(entityName, batchIndex, ex.Message, 0)];
                }

                idBags[batchIndex] = batchIds;
                errorBags[batchIndex] = batchErrors;

                var elapsed = (DateTimeOffset.UtcNow - entityStart).TotalMinutes;
                var added = Interlocked.Add(ref createdCount, batchIds.Count);
                var ratePerMin = elapsed >= 0.001 ? added / elapsed : 0;

                progress?.Report(new BulkCreationProgress
                {
                    EntityLogicalName = entityName,
                    BatchIndex = batchIndex + 1,
                    TotalBatches = batches.Length,
                    RecordsCreated = added,
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
            try
            {
                return await _throttlePolicy.ExecuteAsync(
                    () => CreateMultipleAsync(entityName, batch, ct),
                    entityName, maxRetries, ct).ConfigureAwait(false);
            }
            catch (DataGenerationException ex) when (IsCreateMultipleUnsupported(ex))
            {
                _logger.LogWarning(
                    "CreateMultiple rejected by Dataverse for {Entity} (sdkmessagefilter false positive); falling back to ExecuteMultiple.",
                    entityName);
                _messageChecker.MarkUnsupported(entityName);
            }
        }

        return await _throttlePolicy.ExecuteAsync(
            () => ExecuteMultipleFallbackAsync(entityName, batch, ct),
            entityName, maxRetries, ct).ConfigureAwait(false);
    }

    private static bool IsCreateMultipleUnsupported(DataGenerationException ex) =>
        ex.InnerException is FaultException<OrganizationServiceFault> fault &&
        fault.Detail?.ErrorCode == unchecked((int)0x80040800);

    private async Task<(List<Guid> ids, List<BatchError> errors)> CreateMultipleAsync(
        string entityName,
        Entity[] batch,
        CancellationToken ct)
    {
        var request = new CreateMultipleRequest
        {
            Targets = new EntityCollection(batch.ToList()) { EntityName = entityName }
        };

        var response = (CreateMultipleResponse)await _service.ExecuteAsync(request, ct).ConfigureAwait(false);
        if (response.Ids is null)
            throw new DataGenerationException($"CreateMultiple returned null Ids for '{entityName}'.");
        var ids = response.Ids.ToList();

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

        if (errors.Count > 0)
            _logger.LogWarning(
                "ExecuteMultiple: {Entity} batch had {ErrorCount}/{BatchSize} failures. First error [{Code}]: {Message}",
                entityName, errors.Count, batch.Length, errors[0].FaultCode, errors[0].ErrorMessage);
        else
            _logger.LogDebug("ExecuteMultiple: {Entity} batch of {BatchSize} → {IdCount} IDs.",
                entityName, batch.Length, ids.Count);

        return (ids, errors);
    }

    private AttributeMetadata[] GetGeneratableAttributes(EntityMetadata meta)
    {
        if (meta.Attributes is null) return [];

        return meta.Attributes
            .Where(FieldFilter.ShouldGenerateField)
            .Where(a => _edgeCaseValidator.Validate(a, meta).Action == FieldAction.Generate)
            .ToArray();
    }

    private AttributeMetadata[] GetAlternateKeyAttributes(EntityMetadata meta)
    {
        if (meta.Attributes is null) return [];

        return meta.Attributes
            .Where(FieldFilter.ShouldGenerateField)
            .Where(a =>
            {
                var result = _edgeCaseValidator.Validate(a, meta);
                return result.Action == FieldAction.SpecialHandling
                       && result.HandlingCategory == "AlternateKeyUniqueness";
            })
            .ToArray();
    }

    private AttributeMetadata[] GetRoutableSpecialHandlingAttributes(EntityMetadata meta)
    {
        if (meta.Attributes is null) return [];

        return meta.Attributes
            .Where(FieldFilter.ShouldGenerateField)
            .Where(a =>
            {
                var result = _edgeCaseValidator.Validate(a, meta);
                if (result.Action != FieldAction.SpecialHandling) return false;
                var cat = result.HandlingCategory ?? string.Empty;
                return cat is "MultiSelect" or "PolymorphicLookup" or "OwnerLookup" or "RichText" or "CurrencyValidation"
                       || cat.StartsWith("DateTime_", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
    }

    private async Task PopulateCurrencyPoolAsync(DataverseRecordPool pool, CancellationToken ct)
    {
        try
        {
            var query = new QueryExpression("transactioncurrency")
            {
                ColumnSet = new ColumnSet("transactioncurrencyid"),
                TopCount = 10
            };
            var result = await _service.RetrieveMultipleAsync(query, ct).ConfigureAwait(false);
            if (result.Entities.Count > 0)
            {
                pool.Add("transactioncurrency", result.Entities.Select(e => e.Id));
                _logger.LogInformation("Loaded {Count} transactioncurrency record(s) into pool",
                    result.Entities.Count);
            }
            else
            {
                _logger.LogWarning("No transactioncurrency records found — money fields will be skipped");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to query transactioncurrency pool — money fields may be skipped");
        }
    }

    private static object GenerateUniqueKeyValue(AttributeMetadata attr, string entityName, int recordIndex) =>
        attr switch
        {
            StringAttributeMetadata s => TruncateKey($"{entityName}-{recordIndex:D8}", s.MaxLength ?? 100),
            IntegerAttributeMetadata => recordIndex,
            _ => TruncateKey($"{entityName}-{recordIndex:D8}", 100)
        };

    private static string TruncateKey(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    private static void EnsureThreadPoolTuned()
    {
        if (_threadPoolTuned) return;
        lock (_tuningLock)
        {
            if (_threadPoolTuned) return;
            // Boost minimum threads to reduce latency ramp-up for burst parallel API calls.
            // Program.cs sets the same value at startup; this is a safety net for non-web hosts.
            ThreadPool.SetMinThreads(MinThreadPoolThreads, MinThreadPoolThreads);
            _threadPoolTuned = true;
        }
    }
}

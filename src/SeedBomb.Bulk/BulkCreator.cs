using SeedBomb.Bulk.Contracts;
using SeedBomb.Core.Contracts;
using SeedBomb.Core.EdgeCases;
using SeedBomb.Core.Exceptions;
using SeedBomb.Core.Generators;
using SeedBomb.Core.Graph;
using SeedBomb.Core.Metadata;
using SeedBomb.Core.Rules;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System.Runtime.ExceptionServices;
using System.ServiceModel;

namespace SeedBomb.Bulk;

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
        GenerationLimits.Validate(config);

        PreparedBogusRun? preparedRun = null;
        // WR-002: declared outside the try so a cancel can still report what was written.
        var allCreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.OrdinalIgnoreCase);
        var allErrors = new List<BatchError>();
        var runStart = DateTimeOffset.UtcNow;
        try
        {
            var validatedRules = ValidateConfiguredRules(config, entityMetadata);
            config = config with { FieldRules = validatedRules };

            // Sort first (pure, no I/O): preflight and PreparedLookupRun both need the creation order
            // to know which lookup targets this run creates before their source table.
            var sortedEntities = _topologicalSort.Sort(graph);
            PreflightTables(config, validatedRules, entityMetadata, sortedEntities);

            preparedRun = await PrepareBogusRunOrThrowAsync(config, entityMetadata, ct).ConfigureAwait(false);

            var lookupRun = await PreparedLookupRun.PrepareAsync(config, entityMetadata, sortedEntities,
                _service, _throttlePolicy, _logger, ct).ConfigureAwait(false);

            var pool = new DataverseRecordPool();
            await PopulateCurrencyPoolAsync(pool, ct).ConfigureAwait(false);
            await PopulateSystemUserPoolAsync(pool, ct).ConfigureAwait(false);

            // T-18: default DOP=8; updated after first entity if ServiceClient provides a recommendation
            var effectiveDop = config.MaxParallelism ?? 8;

            if (config.RecordCounts.Values.Sum() >= 5000 && config.MaxParallelism is null)
                await WarmupAndAdoptRecommendedDopAsync(ct).ConfigureAwait(false);

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

                var (createdIds, errors, failure) = await CreateEntityRecordsAsync(
                    entityName, meta, entityIndex, recordCount, config, effectiveDop, pool, preparedRun, lookupRun, progress, ct).ConfigureAwait(false);

                pool.Add(entityName, createdIds);
                allCreatedRecords[entityName] = createdIds.AsReadOnly();
                allErrors.AddRange(errors);
                if (failure is not null)
                    ExceptionDispatchInfo.Capture(failure).Throw();

                _logger.LogInformation(
                    "Completed {Entity}: {Created}/{Requested} records created.",
                    entityName, createdIds.Count, recordCount);

                // After first entity, re-read server-recommended DOP if no user override
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

            // WR-002: a cancel during the last table must not fall through into linking.
            ct.ThrowIfCancellationRequested();

            // Opening Linking snapshot: switches the UI to the link phase even when there is
            // nothing to link, so a run never looks "finished" before phases 2 and 3 have run.
            progress?.Report(new BulkCreationProgress
            {
                Phase = "Linking",
                EntityLogicalName = string.Empty,
                BatchIndex = 0,
                TotalBatches = 0,
                RecordsCreated = 0,
                TotalRecords = 0,
            });

            // Phase 2: backfill deferred lookups
            _logger.LogInformation("Starting Phase 2: deferred lookup backfill.");
            var backfillErrors = await _deferredBackfill.BackfillLookupsAsync(
                graph, pool, config.BatchSize, config.Seed, maxRetries: config.MaxRetries, ct: ct,
                isExplicitLookup: (table, column) => LookupRulePolicy.IsExplicit(config.FieldRules, table, column),
                progress: progress).ConfigureAwait(false);
            allErrors.AddRange(backfillErrors);

            // Phase 3: N:N associations
            var associateErrors = await _deferredBackfill.AssociateManyToManyAsync(
                graph, pool, config.BatchSize, config.Seed, maxRetries: config.MaxRetries, ct: ct,
                progress: progress).ConfigureAwait(false);
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
        catch (Exception ex) when (allCreatedRecords.Values.Any(ids => ids.Count > 0))
        {
            // WR-002 / CR-003: nothing is rolled back — return the rows already written so the
            // summary and History record what landed, whether the run was cancelled or failed.
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            if (!cancelled)
                _logger.LogError(ex, "Bulk creation stopped after {Count} table(s) were written.", allCreatedRecords.Count);
            return new GenerationResult
            {
                CreatedRecords = allCreatedRecords,
                Errors = allErrors.AsReadOnly(),
                Elapsed = DateTimeOffset.UtcNow - runStart,
                Cancelled = cancelled,
                FatalError = cancelled ? null : ex.Message,
            };
        }
        finally
        {
            preparedRun?.Dispose();
        }
    }

    private async Task<(List<Guid> ids, List<BatchError> errors, Exception? failure)> CreateEntityRecordsAsync(
        string entityName,
        EntityMetadata meta,
        int entityIndex,
        int recordCount,
        GenerationConfig config,
        int effectiveDop,
        DataverseRecordPool pool,
        PreparedBogusRun? preparedRun,
        PreparedLookupRun lookupRun,
        IProgress<BulkCreationProgress>? progress,
        CancellationToken ct)
    {
        var tableRules = config.FieldRules is not null
            && config.FieldRules.TryGetValue(entityName, out var configuredRules)
            ? configuredRules
            : new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase);

        // Sequential Bogus generation — single Faker instance, no sharing across threads
        var attributesToGenerate = GetGeneratableAttributes(meta);
        var alternateKeyAttrs = GetAlternateKeyAttributes(meta);
        var specialHandlingAttrs = GetRoutableSpecialHandlingAttributes(meta);
        var coveredLogicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in attributesToGenerate)
        {
            if (attr.LogicalName is not null)
                coveredLogicalNames.Add(attr.LogicalName);
        }
        foreach (var attr in specialHandlingAttrs)
        {
            if (attr.LogicalName is not null)
                coveredLogicalNames.Add(attr.LogicalName);
        }
        var explicitLookupAttrs = (meta.Attributes ?? [])
            .OfType<LookupAttributeMetadata>()
            .Where(a => a.LogicalName is not null
                && !coveredLogicalNames.Contains(a.LogicalName)
                && tableRules.TryGetValue(a.LogicalName, out var ruled)
                && ruled is ConstantRule or OneOfRule or LookupRandomRule or NullRule)
            .ToArray();
        var hasMoney = specialHandlingAttrs.Any(a => a is MoneyAttributeMetadata);
        var faker = DeterministicFaker.Create(config.Seed, entityIndex, config.Locale);
        using var bogusSession = new BogusEvaluatorSession(config.Locale);
        var evalContext = new RuleEvaluationContext(entityName, config.Seed, config.Locale, config.RunId, recordCount);
        var entities = new List<Entity>(recordCount);

        for (int i = 0; i < recordCount; i++)
        {
            var entity = new Entity(entityName);
            foreach (var attr in attributesToGenerate)
            {
                var value = _generatorFactory.Generate(attr, faker, pool); // always consume legacy stream (S7)
                if (attr.LogicalName is not null && tableRules.TryGetValue(attr.LogicalName, out var rule))
                    value = ResolveRuleValue(rule, attr, evalContext, i, preparedRun, lookupRun, pool, bogusSession);
                if (ReferenceEquals(value, RuleValueGenerator.Omit))
                    continue;   // null rule: emit nothing, platform default applies
                if (value is not null)
                    entity[attr.LogicalName!] = value;
            }

            // Special handling attrs that route to generators (DateTime, MultiSelect, PolymorphicLookup, RichText, Money)
            foreach (var attr in specialHandlingAttrs)
            {
                var value = _generatorFactory.Generate(attr, faker, pool); // always consume legacy stream (S7)
                if (attr.LogicalName is not null && tableRules.TryGetValue(attr.LogicalName, out var rule))
                    value = ResolveRuleValue(rule, attr, evalContext, i, preparedRun, lookupRun, pool, bogusSession);
                if (ReferenceEquals(value, RuleValueGenerator.Omit))
                    continue;
                if (value is not null)
                    entity[attr.LogicalName!] = value;
            }

            foreach (var attr in explicitLookupAttrs)
            {
                if (attr.LogicalName is null || !tableRules.TryGetValue(attr.LogicalName, out var rule))
                    continue;
                var value = ResolveRuleValue(rule, attr, evalContext, i, preparedRun, lookupRun, pool, bogusSession);
                if (ReferenceEquals(value, RuleValueGenerator.Omit))
                    continue;
                if (value is not null)
                    entity[attr.LogicalName] = value;
            }

            // Inject transactioncurrencyid for entities with money fields
            if (hasMoney)
            {
                var currencyId = pool.GetRandom("transactioncurrency", faker);
                if (!tableRules.ContainsKey("transactioncurrencyid"))
                {
                    if (currencyId.HasValue)
                        entity["transactioncurrencyid"] = new EntityReference("transactioncurrency", currencyId.Value);
                    else
                        _logger.LogWarning("No transactioncurrency in pool for {Entity} — money fields may be rejected by Dataverse", entityName);
                }
            }

            foreach (var attr in alternateKeyAttrs)
            {
                entity[attr.LogicalName!] = GenerateUniqueKeyValue(attr, entityName, i);
            }
            entities.Add(entity);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            for (int i = 0; i < entities.Count; i++)
            {
                var fields = string.Join(", ", entities[i].Attributes.Select(DescribeField));
                _logger.LogDebug("[Pre-create] {Entity}[{Index}]: {Fields}", entityName, i, fields);
            }
        }

        var batches = entities.Chunk(config.BatchSize).ToArray();

        return await SubmitEntityBatchesAsync(
            entityName, batches, recordCount, config, effectiveDop, progress, ct).ConfigureAwait(false);
    }

    private async Task<(List<Guid> ids, List<BatchError> errors, Exception? failure)> SubmitEntityBatchesAsync(
        string entityName,
        Entity[][] batches,
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

        var submit = Parallel.ForEachAsync(
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
                        entityName, batch, config.MaxRetries, innerCt).ConfigureAwait(false);
                }
                catch (DataGenerationException ex)
                {
                    _logger.LogError(ex, "Batch {BatchIndex}/{TotalBatches} for {Entity} failed",
                        batchIndex + 1, batches.Length, entityName);
                    batchIds = [];
                    batchErrors = [new BatchError(entityName, batchIndex, ex.Message,
                        (ex.InnerException as FaultException<OrganizationServiceFault>)?.Detail?.ErrorCode, batch.Length,
                        ThrottlePolicy.IsTransient(ex.InnerException))];
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
            });

        Exception? failure = null;
        try
        {
            await submit.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // WR-002: keep the IDs of batches that landed before the cancel; CreateAsync reports them.
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        foreach (var idBag in idBags)
            if (idBag is not null) allIds.AddRange(idBag);
        foreach (var errorBag in errorBags)
            if (errorBag is not null) allErrors.AddRange(errorBag);

        return (allIds, allErrors, failure);
    }

    private async Task<(List<Guid> ids, List<BatchError> errors)> SubmitBatchAsync(
        string entityName,
        Entity[] batch,
        int maxRetries,
        CancellationToken ct)
    {
        // No availability pre-check: the sdkmessagefilter probe returned false positives, so the
        // runtime rejection below is the authority. ShouldAttemptCreateMultiple only consults the
        // in-memory cache so a rejected entity skips straight to ExecuteMultiple on later batches.
        if (_messageChecker.ShouldAttemptCreateMultiple(entityName))
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
            catch (DataGenerationException ex) when (ex.InnerException is FaultException<OrganizationServiceFault>)
            {
                _logger.LogWarning(
                    ex,
                    "CreateMultiple failed for {Entity}; performing single ExecuteMultiple fallback batch.",
                    entityName);
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
                       && result.HandlingCategory == SpecialHandlingCategory.AlternateKeyUniqueness;
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
                var cat = result.HandlingCategory;
                // OwnerLookup is intentionally excluded — ownerid is omitted from the Create
                // payload and Dataverse defaults it to the calling user.
                return cat is SpecialHandlingCategory.MultiSelect or SpecialHandlingCategory.PolymorphicLookup
                    or SpecialHandlingCategory.RichText or SpecialHandlingCategory.CurrencyValidation
                    or SpecialHandlingCategory.DateTime;
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

    // Custom lookups pointing at systemuser (approver/manager/requested-by style fields) are never
    // part of the generation graph — SeedBomb doesn't create fake users — so without this pre-seed
    // LookupFieldGenerator.Generate finds an empty pool for "systemuser" and silently leaves the
    // field null. Mirrors PopulateCurrencyPoolAsync above.
    private async Task PopulateSystemUserPoolAsync(DataverseRecordPool pool, CancellationToken ct)
    {
        try
        {
            var query = new QueryExpression("systemuser")
            {
                ColumnSet = new ColumnSet("systemuserid"),
                TopCount = 25
            };
            query.Criteria.AddCondition("isdisabled", ConditionOperator.Equal, false);

            var result = await _service.RetrieveMultipleAsync(query, ct).ConfigureAwait(false);
            if (result.Entities.Count > 0)
            {
                pool.Add("systemuser", result.Entities.Select(e => e.Id));
                _logger.LogInformation("Loaded {Count} systemuser record(s) into pool", result.Entities.Count);
            }
            else
            {
                _logger.LogWarning("No enabled systemuser records found — systemuser lookups will be skipped");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to query systemuser pool — systemuser lookups may be skipped");
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

    private async Task WarmupAndAdoptRecommendedDopAsync(CancellationToken ct)
    {
        try
        {
            await _service.ExecuteAsync(new WhoAmIRequest(), ct).ConfigureAwait(false);
            if (_service is ServiceClient sc && sc.RecommendedDegreesOfParallelism > 0)
            {
                _logger.LogInformation("Warmup completed; server recommended DOP is {Dop}", sc.RecommendedDegreesOfParallelism);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Warmup WhoAmI failed; continuing with default DOP.");
        }
    }

    /// <summary>
    /// Metadata-only checks that must pass before the first write, so a failure leaves the
    /// environment untouched instead of stranding earlier tables (CR-003).
    /// </summary>
    private void PreflightTables(
        GenerationConfig config,
        Dictionary<string, Dictionary<string, FieldRule>> validatedRules,
        IReadOnlyDictionary<string, EntityMetadata> entityMetadata,
        IReadOnlyList<string> sortedEntities)
    {
        foreach (var entityName in config.EntityLogicalNames)
        {
            if (!config.RecordCounts.TryGetValue(entityName, out var count) || count <= 0
                || !entityMetadata.TryGetValue(entityName, out var meta))
                continue;

            // Tables written before this one with a positive count: their records fill its required lookups.
            var position = sortedEntities.ToList().FindIndex(t => string.Equals(t, entityName, StringComparison.OrdinalIgnoreCase));
            var createdEarlier = position < 0
                ? []
                : sortedEntities
                    .Take(position)
                    .Where(t => config.RecordCounts.TryGetValue(t, out var n) && n > 0)
                    .ToArray();

            // T-06: warn about any attributes that will be silently skipped due to FieldAction.Fail
            foreach (var attr in meta.Attributes ?? [])
            {
                if (_edgeCaseValidator.Validate(attr, meta).Action == FieldAction.Fail)
                    _logger.LogWarning(
                        "Entity {Entity}: field {Field} has FieldAction.Fail — field will be skipped",
                        entityName, attr.LogicalName);
            }

            var missing = RequiredLookupPreflight.FindUnsupplied(
                entityName, meta, config.FieldRules?.GetValueOrDefault(entityName), createdEarlier);
            if (missing.Count > 0)
                throw new DataGenerationException(missing[0]);

            // FieldFilter never generates a SystemRequired lookup, so one accepted only because its target
            // is created earlier gets an implicit lookupRandom rule (an explicit rule is never replaced).
            // validatedRules is config.FieldRules, so PreparedLookupRun and generation both see it.
            var implicitLookups = RequiredLookupPreflight.FindSuppliedByCreatedEarlier(
                entityName, meta, validatedRules.GetValueOrDefault(entityName), createdEarlier);
            if (implicitLookups.Count > 0)
            {
                if (!validatedRules.TryGetValue(entityName, out var tableRules))
                    validatedRules[entityName] = tableRules = new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase);
                foreach (var column in implicitLookups)
                    tableRules.TryAdd(column, new LookupRandomRule());
            }

            foreach (var attr in (meta.Attributes ?? []).OfType<LookupAttributeMetadata>())
            {
                if (attr.RequiredLevel?.Value == AttributeRequiredLevel.ApplicationRequired)
                    _logger.LogWarning(
                        "Entity {Entity}: lookup {Field} is ApplicationRequired but may have no generator",
                        entityName, attr.LogicalName);
            }
        }
    }

    private Dictionary<string, Dictionary<string, FieldRule>> ValidateConfiguredRules(
        GenerationConfig config,
        IReadOnlyDictionary<string, EntityMetadata> entityMetadata)
    {
        var validatedRules = new Dictionary<string, Dictionary<string, FieldRule>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityName in config.EntityLogicalNames)
        {
            if (!config.RecordCounts.TryGetValue(entityName, out var recordCount) || recordCount <= 0)
                continue;

            if (config.FieldRules is null)
                continue;

            List<KeyValuePair<string, Dictionary<string, FieldRule>>> tableMatches = [];
            foreach (var pair in config.FieldRules)
            {
                if (string.Equals(pair.Key, entityName, StringComparison.OrdinalIgnoreCase))
                    tableMatches.Add(pair);
            }

            if (tableMatches.Count > 1)
                throw new DataGenerationException(
                    $"Entity '{entityName}': field rules contain duplicate table keys that differ only by case.");
            if (tableMatches.Count == 0)
                continue;

            if (!entityMetadata.TryGetValue(entityName, out var meta))
                throw new DataGenerationException($"Entity '{entityName}': metadata not found.");

            var configuredRules = tableMatches[0].Value;
            var tableRules = new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase);
            var seenColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (logicalName, rule) in configuredRules)
            {
                if (!seenColumns.TryAdd(logicalName, logicalName))
                    throw new DataGenerationException(
                        $"Entity '{entityName}': field rule targets duplicate attribute keys that differ only by case ('{logicalName}').");

                var ruleAttr = meta.Attributes?.FirstOrDefault(a =>
                    string.Equals(a.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
                if (ruleAttr is null)
                    throw new DataGenerationException(
                        $"Entity '{entityName}': field rule targets unknown attribute '{logicalName}'.");

                var validation = RuleValidator.Validate(
                    rule, ruleAttr, new RuleValidationContext(entityName, recordCount, config.RunId, meta));
                if (!validation.IsValid)
                    throw new DataGenerationException(
                        $"Entity '{entityName}': field rule for '{logicalName}' is invalid — " +
                        string.Join(" ", validation.Messages.Select(m => m.Text)));

                tableRules[logicalName] = CloneOwnedRule(validation.EffectiveRule ?? rule);
            }

            validatedRules[entityName] = tableRules;
        }

        return validatedRules;
    }

    private static FieldRule CloneOwnedRule(FieldRule rule) => rule switch
    {
        ConstantRule c => new ConstantRule(c.Value.Clone()),
        OneOfRule o => new OneOfRule(o.Values.Select(static v => v.Clone()).ToArray().AsReadOnly(), o.Pick),
        BogusRule b => new BogusRule(b.Api, b.Endpoint, b.EngineVersion, b.Args),
        _ => rule,
    };

    private static async Task<PreparedBogusRun?> PrepareBogusRunOrThrowAsync(
        GenerationConfig config,
        IReadOnlyDictionary<string, EntityMetadata> entityMetadata,
        CancellationToken ct)
    {
        if (config.FieldRules is null)
            return null;

        var requests = new List<BogusPreparationRequest>();
        foreach (var (table, columns) in config.FieldRules)
        {
            if (!config.RecordCounts.TryGetValue(table, out var recordCount) || recordCount <= 0)
                continue;
            if (!entityMetadata.TryGetValue(table, out var meta))
                continue;

            var attrs = (meta.Attributes ?? [])
                .Where(a => a.LogicalName is not null)
                .ToDictionary(a => a.LogicalName!, StringComparer.OrdinalIgnoreCase);

            var context = new RuleEvaluationContext(table, config.Seed, config.Locale, config.RunId, recordCount);
            foreach (var (column, rule) in columns)
            {
                if (rule is not BogusRule bogus)
                    continue;
                if (!attrs.TryGetValue(column, out var attr))
                    continue;

                requests.Add(new BogusPreparationRequest(bogus, attr, table, column, context));
            }
        }

        if (requests.Count == 0)
            return null;

        var preparation = await BogusRulePreparer.PrepareRun(requests, config.AllowRiskyBogusValues, ct)
            .ConfigureAwait(false);
        if (!preparation.IsBlocked)
            return preparation.Run;

        var detail = string.Join(" ", preparation.Messages
            .Where(m => m.Severity == RuleMessageSeverity.Error)
            .Select(m => m.Text));
        throw new DataGenerationException(
            string.IsNullOrWhiteSpace(detail) ? "Bogus run preparation blocked." : detail);
    }

    private static object? ResolveRuleValue(
        FieldRule rule,
        AttributeMetadata attr,
        RuleEvaluationContext context,
        int rowIndex,
        PreparedBogusRun? preparedRun,
        PreparedLookupRun lookupRun,
        DataverseRecordPool pool,
        BogusEvaluatorSession session)
    {
        if (rule is LookupRandomRule)
            return RuleValueGenerator.EvaluateLookupRandom(
                lookupRun.Get(context.Table, attr.LogicalName!, pool), context.Seed,
                context.Table, attr.LogicalName!, rowIndex);

        if (rule is not BogusRule bogus)
            return RuleValueGenerator.Evaluate(rule, attr, context.Seed, context.Table, rowIndex, context.RunId);

        var column = attr.LogicalName ?? string.Empty;
        if (preparedRun is not null
            && preparedRun.TryGetValue(context.Table, column, context, rowIndex, out var cached)
            && cached is not null)
        {
            return cached;
        }

        PreparedBogusRule compiled;
        if (preparedRun is not null && preparedRun.TryGetCompiled(context.Table, column, out var prepared) && prepared is not null)
            compiled = prepared;
        else
            compiled = BogusRulePreparer.CompileRule(bogus, attr, context);

        return session.Evaluate(compiled, attr, context, rowIndex);
    }

    private static string DescribeField(KeyValuePair<string, object> field)
    {
        var value = field.Value;
        var length = value switch
        {
            string s => s.Length,
            byte[] b => b.Length,
            _ => 0,
        };
        return $"{field.Key}:{value?.GetType().Name ?? "null"}/{length}";
    }
}

using SeedBomb.Bulk;
using SeedBomb.Bulk.Contracts;
using SeedBomb.Core.Contracts;
using SeedBomb.Core.EdgeCases;
using SeedBomb.Core.Generators;
using SeedBomb.Core.Graph;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Web.Services;

/// <summary>
/// Scoped adapter that defers <see cref="BulkCreator"/> construction until the first
/// <see cref="CreateAsync"/> call, at which point the async ServiceClient factory is awaited
/// and the full bulk-creation sub-pipeline is assembled. This allows <see cref="IBulkCreator"/>
/// to be registered in the DI container without requiring a synchronous
/// <see cref="IOrganizationServiceAsync2"/> at scope creation time.
/// </summary>
internal sealed class LazyBulkCreator : IBulkCreator
{
    private readonly Func<CancellationToken, Task<IOrganizationServiceAsync2>> _serviceFactory;
    private readonly GeneratorFactory _generatorFactory;
    private readonly EdgeCaseValidator _edgeCaseValidator;
    private readonly ThrottlePolicy _throttlePolicy;
    private readonly TopologicalSort _topologicalSort;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private BulkCreator? _inner;

    public LazyBulkCreator(
        Func<CancellationToken, Task<IOrganizationServiceAsync2>> serviceFactory,
        GeneratorFactory generatorFactory,
        EdgeCaseValidator edgeCaseValidator,
        ThrottlePolicy throttlePolicy,
        TopologicalSort topologicalSort,
        ILoggerFactory loggerFactory)
    {
        _serviceFactory = serviceFactory;
        _generatorFactory = generatorFactory;
        _edgeCaseValidator = edgeCaseValidator;
        _throttlePolicy = throttlePolicy;
        _topologicalSort = topologicalSort;
        _loggerFactory = loggerFactory;
    }

    private async Task<BulkCreator> GetInnerAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null) return _inner;
            var service = await _serviceFactory(ct).ConfigureAwait(false);
            var messageChecker = new MessageAvailabilityChecker(service, _loggerFactory.CreateLogger<MessageAvailabilityChecker>());
            var deferredBackfill = new DeferredLookupBackfill(service, messageChecker, _throttlePolicy, _loggerFactory.CreateLogger<DeferredLookupBackfill>());
            _inner = new BulkCreator(service, _generatorFactory, _edgeCaseValidator, messageChecker, _throttlePolicy, _topologicalSort, deferredBackfill, _loggerFactory.CreateLogger<BulkCreator>());
            return _inner;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<GenerationResult> CreateAsync(
        GenerationConfig config,
        IReadOnlyDictionary<string, EntityMetadata> entityMetadata,
        DependencyGraph graph,
        IProgress<BulkCreationProgress>? progress = null,
        CancellationToken ct = default)
        => await (await GetInnerAsync(ct).ConfigureAwait(false))
            .CreateAsync(config, entityMetadata, graph, progress, ct)
            .ConfigureAwait(false);
}

using SeedBomb.Core.Contracts;
using SeedBomb.Core.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Web.Services;

/// <summary>
/// Scoped adapter that defers <see cref="DataverseMetadataProvider"/> construction until the first
/// metadata call, at which point the async ServiceClient factory is awaited. This allows
/// <see cref="IMetadataProvider"/> to be registered in the DI container without requiring a
/// synchronous <see cref="IOrganizationServiceAsync2"/> at scope creation time.
/// </summary>
internal sealed class LazyMetadataProvider : IMetadataProvider
{
    private readonly Func<CancellationToken, Task<IOrganizationServiceAsync2>> _serviceFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DataverseMetadataProvider> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private DataverseMetadataProvider? _inner;

    public LazyMetadataProvider(
        Func<CancellationToken, Task<IOrganizationServiceAsync2>> serviceFactory,
        IMemoryCache cache,
        ILogger<DataverseMetadataProvider> logger)
    {
        _serviceFactory = serviceFactory;
        _cache = cache;
        _logger = logger;
    }

    private async Task<DataverseMetadataProvider> GetInnerAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null) return _inner;
            var service = await _serviceFactory(ct).ConfigureAwait(false);
            _inner = new DataverseMetadataProvider(service, _cache, _logger);
            return _inner;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<EntityMetadata> GetEntityAsync(string logicalName, CancellationToken ct = default)
        => await (await GetInnerAsync(ct).ConfigureAwait(false)).GetEntityAsync(logicalName, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<EntityMetadata>> GetEntitiesAsync(string[] logicalNames, CancellationToken ct = default)
        => await (await GetInnerAsync(ct).ConfigureAwait(false)).GetEntitiesAsync(logicalNames, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<EntitySummary>> ListUserEntitiesAsync(CancellationToken ct = default)
        => await (await GetInnerAsync(ct).ConfigureAwait(false)).ListUserEntitiesAsync(ct).ConfigureAwait(false);
}

using DataGen.Core.Contracts;
using DataGen.Core.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Web.Services;

/// <summary>
/// Scoped IMetadataProvider that resolves IOrganizationServiceAsync2 lazily on first use
/// via the async factory delegate, avoiding any sync-over-async at DI construction time.
/// </summary>
internal sealed class LazyMetadataProvider : IMetadataProvider, IDisposable
{
    private readonly Func<CancellationToken, Task<IOrganizationServiceAsync2>> _serviceFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private DataverseMetadataProvider? _inner;
    private MemoryCache? _cache;

    public LazyMetadataProvider(
        Func<CancellationToken, Task<IOrganizationServiceAsync2>> serviceFactory,
        ILoggerFactory loggerFactory)
    {
        _serviceFactory = serviceFactory;
        _loggerFactory = loggerFactory;
    }

    private async Task<DataverseMetadataProvider> GetInnerAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null) return _inner;
            var service = await _serviceFactory(ct).ConfigureAwait(false);
            _cache = new MemoryCache(new MemoryCacheOptions());
            _inner = new DataverseMetadataProvider(service, _cache, _loggerFactory.CreateLogger<DataverseMetadataProvider>());
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

    public void Dispose()
    {
        _cache?.Dispose();
        _initLock.Dispose();
    }
}

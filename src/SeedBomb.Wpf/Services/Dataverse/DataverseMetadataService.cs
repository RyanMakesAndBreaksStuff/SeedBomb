using SeedBomb.Core.Contracts;
using SeedBomb.Core.Metadata;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Services.Dataverse;

/// <summary>
/// Singleton <see cref="IMetadataProvider"/> that defers <see cref="DataverseMetadataProvider"/>
/// construction until the first call, obtaining the service client via
/// <see cref="IDataverseConnectionService"/>. A shared <see cref="MemoryCache"/> is used
/// to avoid redundant Dataverse round-trips within a session.
/// </summary>
public sealed class DataverseMetadataService : IMetadataProvider, IDisposable
{
    private readonly IDataverseConnectionService _conn;
    private readonly ILogger<DataverseMetadataProvider> _innerLogger;
    private volatile MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile DataverseMetadataProvider? _inner;
    private int _disposed;

    /// <summary>Initialises the service.</summary>
    /// <param name="conn">Connection service that supplies the underlying <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/>.</param>
    /// <param name="innerLogger">Logger forwarded to <see cref="DataverseMetadataProvider"/>.</param>
    public DataverseMetadataService(
        IDataverseConnectionService conn,
        ILogger<DataverseMetadataProvider> innerLogger)
    {
        _conn = conn;
        _innerLogger = innerLogger;
        _conn.ConnectionReset += OnConnectionReset;
    }

    /// <inheritdoc />
    public async Task<EntityMetadata> GetEntityAsync(string logicalName, CancellationToken ct = default) =>
        await (await GetInnerAsync(ct).ConfigureAwait(false)).GetEntityAsync(logicalName, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntityMetadata>> GetEntitiesAsync(string[] logicalNames,
        CancellationToken ct = default) =>
        await (await GetInnerAsync(ct).ConfigureAwait(false)).GetEntitiesAsync(logicalNames, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntitySummary>> ListUserEntitiesAsync(CancellationToken ct = default) =>
        await (await GetInnerAsync(ct).ConfigureAwait(false)).ListUserEntitiesAsync(ct).ConfigureAwait(false);

    private async Task<DataverseMetadataProvider> GetInnerAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null) return _inner;
            var service = await _conn.GetOrganizationServiceAsync(ct).ConfigureAwait(false);
            _inner = new DataverseMetadataProvider(service, _cache, _innerLogger);
            return _inner;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void OnConnectionReset(object? sender, EventArgs e) => _ = ResetCacheAsync();

    private async Task ResetCacheAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            // The old cache is left for the GC rather than disposed: reads that started before the
            // reset still hold its provider and write to it, and MemoryCache owns no unmanaged state.
            _cache = new MemoryCache(new MemoryCacheOptions());
            _inner = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _conn.ConnectionReset -= OnConnectionReset;
        // _lock is not disposed: a reset or read may still be waiting on it, and a SemaphoreSlim
        // whose AvailableWaitHandle was never read holds nothing to release.
        _cache.Dispose();
    }
}
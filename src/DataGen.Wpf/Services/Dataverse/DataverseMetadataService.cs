using DataGen.Core.Contracts;
using DataGen.Core.Metadata;
using DataGen.Wpf.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Wpf.Services.Dataverse;

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
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DataverseMetadataProvider? _inner;

    /// <summary>Initialises the service.</summary>
    /// <param name="conn">Connection service that supplies the underlying <see cref="Microsoft.PowerPlatform.Dataverse.Client.ServiceClient"/>.</param>
    /// <param name="innerLogger">Logger forwarded to <see cref="DataverseMetadataProvider"/>.</param>
    public DataverseMetadataService(
        IDataverseConnectionService conn,
        ILogger<DataverseMetadataProvider> innerLogger)
    {
        _conn = conn;
        _innerLogger = innerLogger;
    }

    /// <inheritdoc />
    public async Task<EntityMetadata> GetEntityAsync(string logicalName, CancellationToken ct = default) =>
        await (await GetInnerAsync(ct).ConfigureAwait(false)).GetEntityAsync(logicalName, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntityMetadata>> GetEntitiesAsync(string[] logicalNames, CancellationToken ct = default) =>
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

    /// <inheritdoc />
    public void Dispose()
    {
        _cache.Dispose();
        _lock.Dispose();
    }
}

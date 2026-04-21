using Microsoft.Identity.Web;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataGen.Web.Services;

/// <summary>
/// Scoped-per-circuit factory that creates and caches a Dataverse <see cref="ServiceClient"/>
/// authenticated via the on-behalf-of token flow using Microsoft.Identity.Web v4.
/// </summary>
public sealed class DataverseServiceClientFactory : IServiceClientFactory
{
    private readonly string _dataverseUrl;
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ServiceClient? _cachedClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataverseServiceClientFactory"/> class.
    /// </summary>
    /// <param name="config">Application configuration providing the DataverseUrl.</param>
    /// <param name="tokenAcquisition">Identity.Web token acquisition service.</param>
    public DataverseServiceClientFactory(IConfiguration config, ITokenAcquisition tokenAcquisition)
    {
        _dataverseUrl = config["DataverseUrl"]
            ?? throw new InvalidOperationException("DataverseUrl not configured");
        _tokenAcquisition = tokenAcquisition;
    }

    /// <inheritdoc />
    public async Task<ServiceClient> CreateAsync(CancellationToken ct = default)
    {
        if (_cachedClient is { IsReady: true })
            return _cachedClient;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedClient is { IsReady: true })
                return _cachedClient;

            _cachedClient = new ServiceClient(
                instanceUrl: new Uri(_dataverseUrl),
                tokenProviderFunction: async _ =>
                    await _tokenAcquisition.GetAccessTokenForUserAsync(
                        new[] { $"{_dataverseUrl}/.default" }).ConfigureAwait(false),
                useUniqueInstance: true);

            _cachedClient.EnableAffinityCookie = false;
            return _cachedClient;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _cachedClient?.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}

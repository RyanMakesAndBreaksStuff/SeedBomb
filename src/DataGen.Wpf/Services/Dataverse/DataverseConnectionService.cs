using DataGen.Wpf.Services.Auth;
using DataGen.Wpf.Services.Settings;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataGen.Wpf.Services.Dataverse;

/// <summary>
/// Manages a lazily-created, cached <see cref="ServiceClient"/> connection.
/// Double-checked locking prevents duplicate construction under concurrent callers.
/// </summary>
public sealed class DataverseConnectionService : IDataverseConnectionService, IDisposable
{
    private readonly IAuthService _auth;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ServiceClient? _cached;

    /// <summary>Initialises the service with required dependencies.</summary>
    /// <param name="auth">Auth service used to supply bearer tokens.</param>
    /// <param name="settings">Settings service supplying the org URL.</param>
    public DataverseConnectionService(IAuthService auth, ISettingsService settings)
    {
        _auth = auth;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<IOrganizationServiceAsync2> GetOrganizationServiceAsync(CancellationToken ct = default)
    {
        if (_cached is { IsReady: true })
            return _cached;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { IsReady: true })
                return _cached;

            var s = await _settings.LoadAsync(ct).ConfigureAwait(false);
            var scopes = new[] { $"{s.OrgUrl}/.default" };

            _cached = new ServiceClient(
                instanceUrl: new Uri(s.OrgUrl),
                tokenProviderFunction: async _ => await _auth.GetTokenAsync(scopes, ct).ConfigureAwait(false),
                useUniqueInstance: true);

            // Disable built-in retries — BulkCreator's ThrottlePolicy owns retry logic.
            _cached.MaxRetryCount = 0;
            _cached.RetryPauseTime = TimeSpan.Zero;
            _cached.EnableAffinityCookie = false;

            if (!_cached.IsReady)
                throw new InvalidOperationException(
                    $"ServiceClient failed to connect: {_cached.LastError?.Message}");

            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        _cached?.Dispose();
        _cached = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cached?.Dispose();
        _lock.Dispose();
    }
}

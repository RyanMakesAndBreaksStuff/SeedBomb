using DataGen.Desktop.Services.Auth;
using DataGen.Desktop.Services.Connections;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataGen.Desktop.Services.Dataverse;

/// <summary>
/// Manages a lazily-created, cached <see cref="ServiceClient"/> connection.
/// Double-checked locking prevents duplicate construction under concurrent callers.
/// </summary>
public sealed class DataverseConnectionService : IDataverseConnectionService, IDisposable
{
    private readonly IAuthService _auth;
    private readonly IConnectionProfileService _profileService;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ServiceClient? _cached;

    /// <inheritdoc />
    public event EventHandler? ConnectionReset;

    /// <summary>Initialises the service with required dependencies.</summary>
    /// <param name="auth">Auth service used to supply bearer tokens.</param>
    /// <param name="profileService">Profile service supplying the environment URL.</param>
    public DataverseConnectionService(IAuthService auth, IConnectionProfileService profileService)
    {
        _auth = auth;
        _profileService = profileService;
        _profileService.ProfilesChanged += OnProfilesChanged;
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

            var profile = await _profileService.GetLastUsedAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No connection profile configured.");
            var scopes = new[] { $"{profile.EnvironmentUrl}/.default" };

            _cached = new ServiceClient(
                instanceUrl: new Uri(profile.EnvironmentUrl),
                tokenProviderFunction: async _ => await _auth.GetTokenAsync(scopes, ct).ConfigureAwait(false),
                useUniqueInstance: true);

            // Disable built-in retries — BulkCreator's ThrottlePolicy owns retry logic.
            _cached.MaxRetryCount = 0;
            _cached.RetryPauseTime = TimeSpan.Zero;
            _cached.EnableAffinityCookie = false;

            if (!_cached.IsReady)
                throw new InvalidOperationException(
                    $"ServiceClient failed to connect: {_cached.LastError}");

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
        ServiceClient? cached;
        _lock.Wait();
        try
        {
            cached = _cached;
            _cached = null;
        }
        finally
        {
            _lock.Release();
        }

        cached?.Dispose();
        ConnectionReset?.Invoke(this, EventArgs.Empty);
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Reset();

    /// <inheritdoc />
    public void Dispose()
    {
        _profileService.ProfilesChanged -= OnProfilesChanged;
        _cached?.Dispose();
        _lock.Dispose();
    }
}

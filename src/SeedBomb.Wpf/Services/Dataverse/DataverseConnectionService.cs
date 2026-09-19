using Microsoft.PowerPlatform.Dataverse.Client;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;

namespace Seedbomb.Services.Dataverse;

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
                tokenProviderFunction: CreateTokenProvider(scopes),
                useUniqueInstance: true)
            {
                // Disable built-in retries — BulkCreator's ThrottlePolicy owns retry logic.
                MaxRetryCount = 0,
                RetryPauseTime = TimeSpan.Zero,
                EnableAffinityCookie = false
            };

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

    internal Func<string, Task<string>> CreateTokenProvider(string[] scopes) =>
        async _ => await _auth.GetTokenAsync(scopes, CancellationToken.None).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task ResetAsync()
    {
        ServiceClient? cached;
        // WaitAsync, not Wait: the same semaphore is held across GetOrganizationServiceAsync's
        // network-bound ServiceClient construction, and both callers run on the dispatcher.
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            cached = _cached;
            _cached = null;
        }
        finally
        {
            _lock.Release();
        }

        // ServiceClient.Dispose tears down gRPC/HTTP channels; keep it off the UI thread.
        await Task.Run(() => SafeDispose(cached)).ConfigureAwait(false);
        ConnectionReset?.Invoke(this, EventArgs.Empty);
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => _ = ResetAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        _profileService.ProfilesChanged -= OnProfilesChanged;
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

        SafeDispose(cached);
        _lock.Dispose();
    }

    // ServiceClient.Dispose talks to gRPC/HTTP channels; on process exit those often surface
    // first-chance RPC/COM failures (0x6BA / 0x71A / 0xE0434352) that are not actionable.
    private static void SafeDispose(ServiceClient? client)
    {
        if (client is null) return;
        try
        {
            client.Dispose();
        }
        catch
        {
            // Intentionally ignored — connection is already gone.
        }
    }
}
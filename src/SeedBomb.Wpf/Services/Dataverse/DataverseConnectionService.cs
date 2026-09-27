using Microsoft.PowerPlatform.Dataverse.Client;
using SeedBomb.Services.Auth;

namespace SeedBomb.Services.Dataverse;

/// <summary>
/// Manages a lazily-created, cached <see cref="ServiceClient"/> connection.
/// Double-checked locking prevents duplicate construction under concurrent callers.
/// </summary>
public sealed class DataverseConnectionService : IDataverseConnectionService, IDisposable
{
    private readonly IAuthService _auth;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ServiceClient? _cached;

    /// <inheritdoc />
    public event EventHandler? ConnectionReset;

    /// <summary>
    /// Test seam: given the environment URL, replaces the real client, whose constructor dials the
    /// org. Null builds the real <see cref="ServiceClient"/>.
    /// </summary>
    internal Func<string, ServiceClient>? CreateClientOverride { get; set; }

    /// <summary>Initialises the service with required dependencies.</summary>
    /// <param name="auth">Auth service: supplies the signed-in profile and its bearer tokens.</param>
    public DataverseConnectionService(IAuthService auth)
    {
        _auth = auth;
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

            // CR-002: connect to the environment the live session signed in to. Re-reading last-used
            // is what let a failed or cancelled switch aim writes at a different org.
            var profile = _auth.ActiveProfile
                          ?? throw new InvalidOperationException("Not signed in. Connect to an environment first.");
            var scopes = new[] { $"{profile.EnvironmentUrl}/.default" };

            // WR-006: the constructor signs in and connects synchronously, and the awaits above
            // usually complete inline — so build it on the pool, never on the dispatcher.
            _cached = await Task.Run(() => CreateClientOverride?.Invoke(profile.EnvironmentUrl) ?? new ServiceClient(
                instanceUrl: new Uri(profile.EnvironmentUrl),
                tokenProviderFunction: CreateTokenProvider(scopes),
                useUniqueInstance: true)
            {
                // CR-001: keep the SDK's own retries — they honour Retry-After on service-protection
                // faults, which ThrottlePolicy deliberately does not retry.
                EnableAffinityCookie = false
            }, ct).ConfigureAwait(false);

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

    /// <inheritdoc />
    public void Dispose()
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
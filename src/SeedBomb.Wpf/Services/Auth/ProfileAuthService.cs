using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Seedbomb.Services.Connections;

namespace Seedbomb.Services.Auth;

/// <summary>
/// <see cref="IAuthService"/> implementation that authenticates against the
/// active <see cref="ConnectionProfile"/> managed by <see cref="IConnectionProfileService"/>.
/// </summary>
public sealed class ProfileAuthService : IAuthService, IDisposable
{
    /// <summary>
    /// A cached MSAL client (IPublicClientApplication or IConfidentialClientApplication)
    /// alongside the fingerprint of the credential it was built from, so a stale client
    /// can be detected and rebuilt when the profile's credential changes.
    /// </summary>
    private sealed record CachedClient(object Client, string Fingerprint);

    private readonly IConnectionProfileService _profiles;
    private readonly Dictionary<Guid, CachedClient> _clients = [];
    private IAccount? _account;
    private Guid? _activeProfileId;
    private MsalCacheHelper? _userCacheHelper;
    private MsalCacheHelper? _appCacheHelper;
    private readonly ILogger<ProfileAuthService>? _logger;

    /// <summary>
    /// Test seam: when set, replaces real confidential-client construction (which otherwise
    /// loads a certificate from the CurrentUser store or performs real MSAL builder work).
    /// Null uses the real MSAL builder.
    /// </summary>
    internal Func<ConnectionProfile, IConfidentialClientApplication>? CreateCcaOverride { get; set; }

    /// <summary>
    /// Test seam: when set, replaces real public-client construction. Null uses the real
    /// MSAL builder.
    /// </summary>
    internal Func<ConnectionProfile, IPublicClientApplication>? CreatePcaOverride { get; set; }

    /// <summary>Initialises the service and subscribes to profile changes.</summary>
    /// <param name="profiles">Connection profile store.</param>
    /// <param name="logger">Optional logger. Tests may omit it.</param>
    public ProfileAuthService(
        IConnectionProfileService profiles,
        ILogger<ProfileAuthService>? logger = null)
    {
        _profiles = profiles;
        _logger = logger;
        _profiles.ProfilesChanged += OnProfilesChanged;
    }

    /// <inheritdoc />
    public string? CurrentUserDisplayName => _account?.Username;

    /// <summary>True when no MSAL client applications are cached. Exposed for tests.</summary>
    internal bool HasNoCachedClients => _clients.Count == 0;

    internal static bool ShouldDropSession(Guid? activeProfileId, IEnumerable<Guid> remainingIds) =>
        activeProfileId is Guid id && remainingIds.All(x => x != id);

    internal static StorageCreationProperties CreateUserCacheProperties()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataGen");
        return new StorageCreationPropertiesBuilder("msal_user_cache.bin", dir).Build();
    }

    // MSAL rejects a single flat file backing both a public-client (user) and confidential-client
    // (app-only) token cache: MsalClientException "combined_user_app_cache_not_supported". Each
    // client type needs its own cache file.
    internal static StorageCreationProperties CreateAppCacheProperties()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataGen");
        return new StorageCreationPropertiesBuilder("msal_app_cache.bin", dir).Build();
    }

    private async Task<MsalCacheHelper?> GetUserCacheHelperAsync()
    {
        if (_userCacheHelper is not null)
            return _userCacheHelper;

        try
        {
            _userCacheHelper = await MsalCacheHelper.CreateAsync(CreateUserCacheProperties()).ConfigureAwait(false);
            return _userCacheHelper;
        }
        catch (Exception ex)
        {
            // WR-011: returning null is correct — auth degrades to interactive-every-launch.
            // Silently is not: this is the only signal that the token cache is broken.
            _logger?.LogWarning(ex,
                "MSAL user token cache could not be initialised; sign-in will be interactive every launch");
            return null;
        }
    }

    private async Task<MsalCacheHelper?> GetAppCacheHelperAsync()
    {
        if (_appCacheHelper is not null)
            return _appCacheHelper;

        try
        {
            _appCacheHelper = await MsalCacheHelper.CreateAsync(CreateAppCacheProperties()).ConfigureAwait(false);
            return _appCacheHelper;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "MSAL app token cache could not be initialised; app-only auth will re-acquire tokens every launch");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult> TryConnectAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            ValidateProfile(profile);
            return await AuthenticateCoreAsync(profile, parentHwnd, commitSession: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new AuthResult(false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult> SignInAsync(nint parentHwnd, CancellationToken ct = default)
    {
        var profile = await _profiles.GetLastUsedAsync(ct).ConfigureAwait(false);
        if (profile is null)
            return new AuthResult(false, null, "No connection profile configured. Open Connection Manager to create one.");

        try
        {
            ValidateProfile(profile);
            await _profiles.SetLastUsedAsync(profile.Id, ct).ConfigureAwait(false);
            return await AuthenticateCoreAsync(profile, parentHwnd, commitSession: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new AuthResult(false, null, ex.Message);
        }
    }

    private async Task<AuthResult> AuthenticateCoreAsync(
        ConnectionProfile profile, nint parentHwnd, bool commitSession, CancellationToken ct)
    {
        var result = profile.AuthType switch
        {
            AuthType.OAuth => await SignInOAuthAsync(profile, parentHwnd, commitSession, ct).ConfigureAwait(false),
            AuthType.ClientSecret or AuthType.Certificate => await SignInAppOnlyAsync(profile, commitSession, ct).ConfigureAwait(false),
            _ => new AuthResult(false, null, $"Unknown auth type: {profile.AuthType}"),
        };

        if (result.Succeeded && commitSession)
            _activeProfileId = profile.Id;

        return result;
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default)
    {
        if (_activeProfileId is null || !_clients.TryGetValue(_activeProfileId.Value, out var cached))
            throw new InvalidOperationException("Not signed in.");

        AuthenticationResult result;
        if (cached.Client is IPublicClientApplication pca)
            result = await pca.AcquireTokenSilent(scopes, _account).ExecuteAsync(ct).ConfigureAwait(false);
        else if (cached.Client is IConfidentialClientApplication cca)
            result = await cca.AcquireTokenForClient(scopes).ExecuteAsync(ct).ConfigureAwait(false);
        else
            throw new InvalidOperationException("Unknown MSAL client type.");

        return result.AccessToken;
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        // PR-007: clearing _account alone leaves cached confidential clients able to keep
        // minting app-only tokens. Drop every client and the active profile too.
        foreach (var client in _clients.Values.Select(c => c.Client).OfType<IPublicClientApplication>())
        {
            foreach (var account in await client.GetAccountsAsync().ConfigureAwait(false))
            {
                try
                {
                    await client.RemoveAsync(account).ConfigureAwait(false);
                }
                catch (MsalException)
                {
                    // Account already gone from the cache; nothing to remove.
                }
            }
        }

        _clients.Clear();
        _account = null;
        _activeProfileId = null;
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? SignedOut;

    private async Task<AuthResult> SignInOAuthAsync(
        ConnectionProfile profile, nint parentHwnd, bool commitSession, CancellationToken ct)
    {
        var pca = await GetOrCreatePca(profile, commitSession).ConfigureAwait(false);
        var scopes = new[] { $"{profile.EnvironmentUrl}/.default" };

        try
        {
            var accounts = await pca.GetAccountsAsync().ConfigureAwait(false);
            IAccount? account = accounts.FirstOrDefault();

            if (account is not null)
            {
                var silent = await pca.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
                account = silent.Account;
                if (commitSession)
                    _account = account;
                return new AuthResult(true, silent.Account.Username, null);
            }

            if (parentHwnd == nint.Zero)
                return new AuthResult(false, null, "No cached session. Please sign in.");

            var interactive = await pca.AcquireTokenInteractive(scopes)
                .WithParentActivityOrWindow(parentHwnd)
                .ExecuteAsync(ct).ConfigureAwait(false);
            account = interactive.Account;
            if (commitSession)
                _account = account;
            return new AuthResult(true, interactive.Account.Username, null);
        }
        catch (MsalUiRequiredException)
        {
            if (parentHwnd == nint.Zero)
                return new AuthResult(false, null, "Session expired. Please sign in again.");

            try
            {
                var interactive = await pca.AcquireTokenInteractive(scopes)
                    .WithParentActivityOrWindow(parentHwnd)
                    .ExecuteAsync(ct).ConfigureAwait(false);
                if (commitSession)
                    _account = interactive.Account;
                return new AuthResult(true, interactive.Account.Username, null);
            }
            catch (MsalException ex2)
            {
                return new AuthResult(false, null, ex2.Message);
            }
        }
        catch (MsalException ex)
        {
            return new AuthResult(false, null, ex.Message);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // WAM/broker RPC (0x6BA / 0x71A) when account service is unavailable — treat as no session.
            return new AuthResult(false, null, ex.Message);
        }
    }

    private async Task<AuthResult> SignInAppOnlyAsync(
        ConnectionProfile profile, bool commitSession, CancellationToken ct)
    {
        if (profile.AuthType == AuthType.Certificate)
        {
            if (string.IsNullOrWhiteSpace(profile.CertificateThumbprint))
                return new AuthResult(false, null, "Certificate thumbprint is not configured for this profile.");
        }
        else if (string.IsNullOrWhiteSpace(profile.ClientSecret))
        {
            return new AuthResult(false, null, "Client Secret is not configured for this profile.");
        }

        var cca = await GetOrCreateCca(profile, commitSession).ConfigureAwait(false);
        var scopes = new[] { $"{profile.EnvironmentUrl}/.default" };

        try
        {
            var result = await cca.AcquireTokenForClient(scopes).ExecuteAsync(ct).ConfigureAwait(false);
            return new AuthResult(true, profile.Name, null);
        }
        catch (MsalException ex)
        {
            return new AuthResult(false, null, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new AuthResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Fingerprint of the credential a public client is built from: the client ID plus the
    /// auth type. A cached client whose fingerprint doesn't match the profile's current
    /// values is stale and must be rebuilt.
    /// </summary>
    private static string ComputePcaFingerprint(ConnectionProfile profile) =>
        $"{profile.AuthType}:{profile.ClientId}";

    /// <summary>
    /// Fingerprint of the credential a confidential client is built from: the auth type plus
    /// a SHA-256 hash of the secret or certificate thumbprint currently in use. Hashing keeps
    /// the raw secret out of the cache key/comparison state.
    /// </summary>
    private static string ComputeCcaFingerprint(ConnectionProfile profile)
    {
        var credential = profile.AuthType == AuthType.Certificate
            ? profile.CertificateThumbprint
            : profile.ClientSecret;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credential ?? string.Empty));
        return $"{profile.AuthType}:{Convert.ToHexString(hash)}";
    }

    internal async Task<IPublicClientApplication> GetOrCreatePca(ConnectionProfile profile, bool commitSession)
    {
        var fingerprint = ComputePcaFingerprint(profile);

        if (commitSession
            && _clients.TryGetValue(profile.Id, out var existing)
            && existing.Client is IPublicClientApplication cachedPca
            && existing.Fingerprint == fingerprint)
        {
            return cachedPca;
        }

        IPublicClientApplication newPca;
        if (CreatePcaOverride is not null)
        {
            newPca = CreatePcaOverride(profile);
        }
        else
        {
            var authority = string.IsNullOrWhiteSpace(profile.TenantId)
                ? "https://login.microsoftonline.com/common"
                : $"https://login.microsoftonline.com/{profile.TenantId}";

            newPca = PublicClientApplicationBuilder
                .Create(profile.ClientId)
                .WithAuthority(authority)
                .WithDefaultRedirectUri()
                .Build();

            var helper = await GetUserCacheHelperAsync().ConfigureAwait(false);
            helper?.RegisterCache(newPca.UserTokenCache);
        }

        if (commitSession)
            _clients[profile.Id] = new CachedClient(newPca, fingerprint);

        return newPca;
    }

    internal async Task<IConfidentialClientApplication> GetOrCreateCca(ConnectionProfile profile, bool commitSession)
    {
        var fingerprint = ComputeCcaFingerprint(profile);

        if (commitSession
            && _clients.TryGetValue(profile.Id, out var existing)
            && existing.Client is IConfidentialClientApplication cachedCca
            && existing.Fingerprint == fingerprint)
        {
            return cachedCca;
        }

        IConfidentialClientApplication newCca;
        if (CreateCcaOverride is not null)
        {
            newCca = CreateCcaOverride(profile);
        }
        else
        {
            var authority = string.IsNullOrWhiteSpace(profile.TenantId)
                ? "https://login.microsoftonline.com/common"
                : $"https://login.microsoftonline.com/{profile.TenantId}";

            var ccaBuilder = ConfidentialClientApplicationBuilder
                .Create(profile.ClientId)
                .WithAuthority(authority);

            ccaBuilder = profile.AuthType == AuthType.Certificate
                ? ccaBuilder.WithCertificate(CertificateLoader.Load(profile.CertificateThumbprint!))
                : ccaBuilder.WithClientSecret(profile.ClientSecret!);

            newCca = ccaBuilder.Build();

            var helper = await GetAppCacheHelperAsync().ConfigureAwait(false);
            helper?.RegisterCache(newCca.AppTokenCache);
        }

        if (commitSession)
            _clients[profile.Id] = new CachedClient(newCca, fingerprint);

        return newCca;
    }

    private static void ValidateProfile(ConnectionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.EnvironmentUrl))
            throw new InvalidOperationException("Environment URL is not configured.");

        if (string.IsNullOrWhiteSpace(profile.ClientId))
            throw new InvalidOperationException("Client ID is not configured.");

        if (!Guid.TryParse(profile.ClientId, out _))
            throw new InvalidOperationException($"Client ID '{profile.ClientId}' is not a valid GUID.");

        // TenantId is optional for OAuth (falls back to /common endpoint)
        if (profile.AuthType != AuthType.OAuth)
        {
            if (string.IsNullOrWhiteSpace(profile.TenantId))
                throw new InvalidOperationException("Tenant ID is not configured.");

            if (!Guid.TryParse(profile.TenantId, out _))
                throw new InvalidOperationException($"Tenant ID '{profile.TenantId}' is not a valid GUID.");
        }
    }

    private void OnProfilesChanged(object? sender, EventArgs e) =>
        _ = ReconcileSessionAsync();

    private async Task ReconcileSessionAsync()
    {
        var active = _activeProfileId;
        if (active is null)
            return;

        try
        {
            var remaining = await _profiles.GetAllAsync().ConfigureAwait(false);
            if (!ShouldDropSession(active, remaining.Select(p => p.Id)))
                return;
        }
        catch
        {
            return;
        }

        _clients.Clear();
        _account = null;
        _activeProfileId = null;
    }

    /// <inheritdoc />
    public void Dispose() => _profiles.ProfilesChanged -= OnProfilesChanged;
}

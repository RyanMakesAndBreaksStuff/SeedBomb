using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SeedBomb.Services.Auth;

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
    // IN-003: written after ConfigureAwait(false) continuations and cleared from pool threads.
    private readonly ConcurrentDictionary<Guid, CachedClient> _clients = new();
    private IAccount? _account;
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

    /// <inheritdoc />
    /// <remarks>The internal setter is a test seam: MSAL's token builders cannot be faked.</remarks>
    public ConnectionProfile? ActiveProfile { get; internal set; }

    /// <summary>True when no MSAL client applications are cached. Exposed for tests.</summary>
    internal bool HasNoCachedClients => _clients.Count == 0;

    internal static bool ShouldDropSession(Guid? activeProfileId, IEnumerable<Guid> remainingIds) =>
        activeProfileId is Guid id && remainingIds.All(x => x != id);

    internal static StorageCreationProperties CreateUserCacheProperties()
    {
        var dir = AppPaths.Root;
        return new StorageCreationPropertiesBuilder("msal_user_cache.bin", dir).Build();
    }

    // MSAL rejects a single flat file backing both a public-client (user) and confidential-client
    // (app-only) token cache: MsalClientException "combined_user_app_cache_not_supported". Each
    // client type needs its own cache file.
    internal static StorageCreationProperties CreateAppCacheProperties()
    {
        var dir = AppPaths.Root;
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
    public async Task<AuthResult> TryConnectAsync(ConnectionProfile profile, nint parentHwnd,
        CancellationToken ct = default)
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
        // CR-006: never throws — startup has no other handler, so a store or credential failure
        // must come back as a result the sign-in overlay can show.
        try
        {
            var profile = await _profiles.GetLastUsedAsync(ct).ConfigureAwait(false);
            return profile is null
                ? new AuthResult(false, null, "No connection profile configured. Open Connection Manager to create one.")
                : await SignInAsync(profile, parentHwnd, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AuthResult(false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult> SignInAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            ValidateProfile(profile);
            return await AuthenticateCoreAsync(profile, parentHwnd, commitSession: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
            AuthType.ClientSecret or AuthType.Certificate => await SignInAppOnlyAsync(profile, commitSession, ct)
                .ConfigureAwait(false),
            _ => new AuthResult(false, null, $"Unknown auth type: {profile.AuthType}"),
        };

        if (result.Succeeded && commitSession)
        {
            ActiveProfile = profile;
            // CR-002: last-used moves only after a successful sign-in. It just picks what the next
            // launch tries, so a failed write is logged rather than failing a session that is live.
            try
            {
                await _profiles.SetLastUsedAsync(profile.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Could not record {Profile} as the last-used connection", profile.Name);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default)
    {
        if (ActiveProfile is null || !_clients.TryGetValue(ActiveProfile.Id, out var cached))
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
        ActiveProfile = null;
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? SignedOut;

    /// <inheritdoc />
    public async Task ForgetProfileAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AuthType != AuthType.OAuth || profile.HomeAccountId is not { } homeAccountId)
            return;

        try
        {
            // Reuses the same client-building path as sign-in (GetOrCreatePca); commitSession:
            // false builds a throwaway client (like TestConnectionAsync) instead of touching
            // _clients, since the profile is about to be deleted.
            var pca = await GetOrCreatePca(profile, commitSession: false).ConfigureAwait(false);
            var account = await pca.GetAccountAsync(homeAccountId).ConfigureAwait(false);
            if (account is not null)
                await pca.RemoveAsync(account).ConfigureAwait(false);
        }
        catch (MsalException ex)
        {
            _logger?.LogWarning(ex, "Could not remove the cached account for deleted profile {Profile}", profile.Name);
        }
    }

    private async Task<AuthResult> SignInOAuthAsync(
        ConnectionProfile profile, nint parentHwnd, bool commitSession, CancellationToken ct)
    {
        var pca = await GetOrCreatePca(profile, commitSession).ConfigureAwait(false);
        var scopes = new[] { $"{profile.EnvironmentUrl}/.default" };

        try
        {
            // WR-005: every OAuth profile shares msal_user_cache.bin, so "the first cached account"
            // can be another profile's user. Use only the account this profile signed in as.
            var account = profile.HomeAccountId is { } homeAccountId
                ? await pca.GetAccountAsync(homeAccountId).ConfigureAwait(false)
                : null;

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
            {
                _account = account;
                await RememberAccountAsync(profile, account, ct).ConfigureAwait(false);
            }
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
                {
                    _account = interactive.Account;
                    await RememberAccountAsync(profile, interactive.Account, ct).ConfigureAwait(false);
                }
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

    // WR-005: record which cached account belongs to this profile so the next silent sign-in asks
    // for exactly that one. Only a hint for next time: a failed write is logged, not surfaced.
    private async Task RememberAccountAsync(ConnectionProfile profile, IAccount account, CancellationToken ct)
    {
        var homeAccountId = account.HomeAccountId?.Identifier;
        if (homeAccountId is null || homeAccountId == profile.HomeAccountId)
            return;

        profile.HomeAccountId = homeAccountId;
        try
        {
            await _profiles.SaveAsync(profile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Could not record the signed-in account for {Profile}", profile.Name);
        }
    }

    private async Task<AuthResult> SignInAppOnlyAsync(
        ConnectionProfile profile, bool commitSession, CancellationToken ct)
    {
        string? clientSecret = profile.ClientSecret;
        if (profile.AuthType == AuthType.Certificate)
        {
            if (string.IsNullOrWhiteSpace(profile.CertificateThumbprint))
                return new AuthResult(false, null, "Certificate thumbprint is not configured for this profile.");
        }
        else
        {
            clientSecret ??= await _profiles.GetSecretAsync(profile.Id, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(clientSecret))
                return new AuthResult(false, null,
                    "Re-enter the client secret for this connection — none is saved, or the saved one can't be decrypted for this Windows user.");
        }

        var cca = await GetOrCreateCca(profile, commitSession, clientSecret).ConfigureAwait(false);
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
    private static string ComputeCcaFingerprint(ConnectionProfile profile, string? clientSecret)
    {
        var credential = profile.AuthType == AuthType.Certificate
            ? profile.CertificateThumbprint
            : clientSecret;
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
            newPca = PublicClientApplicationBuilder
                .Create(profile.ClientId)
                .WithAuthority(ResolveCloud(profile), ResolveTenant(profile))
                .WithDefaultRedirectUri()
                .Build();

            var helper = await GetUserCacheHelperAsync().ConfigureAwait(false);
            helper?.RegisterCache(newPca.UserTokenCache);
        }

        if (commitSession)
            _clients[profile.Id] = new CachedClient(newPca, fingerprint);

        return newPca;
    }

    internal async Task<IConfidentialClientApplication> GetOrCreateCca(
        ConnectionProfile profile, bool commitSession, string? clientSecret = null)
    {
        clientSecret ??= profile.ClientSecret;
        var fingerprint = ComputeCcaFingerprint(profile, clientSecret);

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
            var ccaBuilder = ConfidentialClientApplicationBuilder
                .Create(profile.ClientId)
                .WithAuthority(ResolveCloud(profile), ResolveTenant(profile));

            ccaBuilder = profile.AuthType == AuthType.Certificate
                ? ccaBuilder.WithCertificate(CertificateLoader.Load(profile.CertificateThumbprint!))
                : ccaBuilder.WithClientSecret(clientSecret!);

            newCca = ccaBuilder.Build();

            var helper = await GetAppCacheHelperAsync().ConfigureAwait(false);
            helper?.RegisterCache(newCca.AppTokenCache);
        }

        if (commitSession)
            _clients[profile.Id] = new CachedClient(newCca, fingerprint);

        return newCca;
    }

    /// <summary>
    /// Maps a profile's Dataverse host suffix onto its Entra cloud. Public cloud is the
    /// fallback for anything unrecognised, matching the previous hardcoded behaviour.
    /// </summary>
    /// <param name="profile">Profile whose <see cref="ConnectionProfile.EnvironmentUrl"/> selects the cloud.</param>
    internal static AzureCloudInstance ResolveCloud(ConnectionProfile profile)
    {
        if (!Uri.TryCreate(profile.EnvironmentUrl, UriKind.Absolute, out var uri))
            return AzureCloudInstance.AzurePublic;

        return uri.Host switch
        {
            var h when h.EndsWith(".crm.microsoftdynamics.us", StringComparison.OrdinalIgnoreCase)
                       || h.EndsWith(".crm.appsplatform.us", StringComparison.OrdinalIgnoreCase)
                => AzureCloudInstance.AzureUsGovernment,
            var h when h.EndsWith(".crm.dynamics.cn", StringComparison.OrdinalIgnoreCase)
                => AzureCloudInstance.AzureChina,
            _ => AzureCloudInstance.AzurePublic,
        };
    }

    /// <summary>Tenant segment for the authority: the profile's tenant, or "common" when blank.</summary>
    private static string ResolveTenant(ConnectionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.TenantId) ? "common" : profile.TenantId;

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
        var active = ActiveProfile?.Id;
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
        ActiveProfile = null;
        // CR-002: say so — the shell still showed the deleted profile as connected.
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose() => _profiles.ProfilesChanged -= OnProfilesChanged;
}

using DataGen.Desktop.Services.Connections;
using Microsoft.Identity.Client;

namespace DataGen.Desktop.Services.Auth;

/// <summary>
/// <see cref="IAuthService"/> implementation that authenticates against the
/// active <see cref="ConnectionProfile"/> managed by <see cref="IConnectionProfileService"/>.
/// </summary>
public sealed class ProfileAuthService : IAuthService, IDisposable
{
    private readonly IConnectionProfileService _profiles;
    // Values are IPublicClientApplication or IConfidentialClientApplication.
    private readonly Dictionary<Guid, object> _clients = [];
    private IAccount? _account;
    private Guid? _activeProfileId;

    /// <summary>Initialises the service and subscribes to profile changes.</summary>
    public ProfileAuthService(IConnectionProfileService profiles)
    {
        _profiles = profiles;
        _profiles.ProfilesChanged += OnProfilesChanged;
    }

    /// <inheritdoc />
    public string? CurrentUserDisplayName => _account?.Username;

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
            _activeProfileId = profile.Id;

            return profile.AuthType switch
            {
                AuthType.OAuth        => await SignInOAuthAsync(profile, parentHwnd, ct).ConfigureAwait(false),
                AuthType.ClientSecret => await SignInClientSecretAsync(profile, ct).ConfigureAwait(false),
                AuthType.UserPassword => await SignInUserPasswordAsync(profile, ct).ConfigureAwait(false),
                _                     => new AuthResult(false, null, $"Unknown auth type: {profile.AuthType}")
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new AuthResult(false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default)
    {
        if (_activeProfileId is null || !_clients.TryGetValue(_activeProfileId.Value, out var client))
            throw new InvalidOperationException("Not signed in.");

        AuthenticationResult result;
        if (client is IPublicClientApplication pca)
            result = await pca.AcquireTokenSilent(scopes, _account).ExecuteAsync(ct).ConfigureAwait(false);
        else if (client is IConfidentialClientApplication cca)
            result = await cca.AcquireTokenForClient(scopes).ExecuteAsync(ct).ConfigureAwait(false);
        else
            throw new InvalidOperationException("Unknown MSAL client type.");

        return result.AccessToken;
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (_account is not null && _activeProfileId is not null
            && _clients.TryGetValue(_activeProfileId.Value, out var signOutClient)
            && signOutClient is IPublicClientApplication pca)
        {
            await pca.RemoveAsync(_account).ConfigureAwait(false);
        }
        _account = null;
    }

    private async Task<AuthResult> SignInOAuthAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct)
    {
        var pca = GetOrCreatePca(profile);
        var scopes = new[] { $"{profile.EnvironmentUrl}/.default" };

        try
        {
            var accounts = await pca.GetAccountsAsync().ConfigureAwait(false);
            _account = accounts.FirstOrDefault();

            if (_account is not null)
            {
                var silent = await pca.AcquireTokenSilent(scopes, _account).ExecuteAsync(ct).ConfigureAwait(false);
                _account = silent.Account;
                return new AuthResult(true, silent.Account.Username, null);
            }

            if (parentHwnd == nint.Zero)
                return new AuthResult(false, null, "No cached session. Please sign in.");

            var interactive = await pca.AcquireTokenInteractive(scopes)
                .WithParentActivityOrWindow(parentHwnd)
                .ExecuteAsync(ct).ConfigureAwait(false);
            _account = interactive.Account;
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
    }

    private async Task<AuthResult> SignInClientSecretAsync(ConnectionProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ClientSecret))
            return new AuthResult(false, null, "Client Secret is not configured for this profile.");

        var cca = GetOrCreateCca(profile);
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
    }

    private async Task<AuthResult> SignInUserPasswordAsync(ConnectionProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.Username) || string.IsNullOrWhiteSpace(profile.Password))
            return new AuthResult(false, null, "Username or Password is not configured for this profile.");

        var pca = GetOrCreatePca(profile);
        var scopes = new[] { $"{profile.EnvironmentUrl}/.default" };

        try
        {
#pragma warning disable CS0618 // AcquireTokenByUsernamePassword deprecated — intentional ROPC support
            var result = await pca.AcquireTokenByUsernamePassword(scopes, profile.Username, profile.Password)
                .ExecuteAsync(ct).ConfigureAwait(false);
#pragma warning restore CS0618
            _account = result.Account;
            return new AuthResult(true, result.Account.Username, null);
        }
        catch (MsalException ex)
        {
            return new AuthResult(false, null, ex.Message);
        }
    }

    private IPublicClientApplication GetOrCreatePca(ConnectionProfile profile)
    {
        if (_clients.TryGetValue(profile.Id, out var existing) && existing is IPublicClientApplication pca)
            return pca;

        var newPca = PublicClientApplicationBuilder
            .Create(profile.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{profile.TenantId}")
            .WithDefaultRedirectUri()
            .Build();
        _clients[profile.Id] = newPca;
        return newPca;
    }

    private IConfidentialClientApplication GetOrCreateCca(ConnectionProfile profile)
    {
        if (_clients.TryGetValue(profile.Id, out var existing) && existing is IConfidentialClientApplication cca)
            return cca;

        var newCca = ConfidentialClientApplicationBuilder
            .Create(profile.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{profile.TenantId}")
            .WithClientSecret(profile.ClientSecret!)
            .Build();
        _clients[profile.Id] = newCca;
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

        if (string.IsNullOrWhiteSpace(profile.TenantId))
            throw new InvalidOperationException("Tenant ID is not configured.");

        if (!Guid.TryParse(profile.TenantId, out _))
            throw new InvalidOperationException($"Tenant ID '{profile.TenantId}' is not a valid GUID.");
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        _clients.Clear();
        _account = null;
        _activeProfileId = null;
    }

    /// <inheritdoc />
    public void Dispose() => _profiles.ProfilesChanged -= OnProfilesChanged;
}

using DataGen.Desktop.Services.Settings;
using Microsoft.Identity.Client;

namespace DataGen.Desktop.Services.Auth;

/// <summary>
/// MSAL public-client authentication service. Attempts silent token acquisition first;
/// falls back to interactive login when silent acquisition fails.
/// </summary>
public sealed class MsalAuthService : IAuthService
{
    private readonly string _clientId;
    private readonly string _tenantId;
    private readonly string _orgUrl;
    private IPublicClientApplication? _pca;
    private IAccount? _account;

    /// <summary>
    /// Initialises the MSAL public client using settings loaded synchronously at startup.
    /// </summary>
    /// <param name="settings">Settings service supplying ClientId, TenantId, and OrgUrl.</param>
    public MsalAuthService(ISettingsService settings)
    {
        // Load synchronously once at startup — settings file is tiny and read-only here.
        var s = settings.LoadAsync().GetAwaiter().GetResult();
        _clientId = s.ClientId;
        _tenantId = s.TenantId;
        _orgUrl = s.OrgUrl;
    }

    private IPublicClientApplication EnsurePca()
    {
        if (_pca is not null)
            return _pca;

        if (string.IsNullOrWhiteSpace(_clientId))
            throw new InvalidOperationException("Client ID is not configured. Open Settings and enter your Entra ID application ID.");

        if (!Guid.TryParse(_clientId, out _))
            throw new InvalidOperationException($"Client ID '{_clientId}' is not a valid GUID. Open Settings and correct your Entra ID application ID.");

        if (string.IsNullOrWhiteSpace(_tenantId))
            throw new InvalidOperationException("Tenant ID is not configured. Open Settings and enter your Entra ID tenant ID.");

        if (!Guid.TryParse(_tenantId, out _))
            throw new InvalidOperationException($"Tenant ID '{_tenantId}' is not a valid GUID. Open Settings and correct your Entra ID tenant ID.");

        _pca = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority($"https://login.microsoftonline.com/{_tenantId}")
            .WithDefaultRedirectUri()
            .Build();

        return _pca;
    }

    /// <inheritdoc />
    public string? CurrentUserDisplayName => _account?.Username;

    /// <inheritdoc />
    public async Task<AuthResult> SignInAsync(nint parentHwnd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_orgUrl))
            return new AuthResult(false, null, "Organization URL is not configured. Open Settings and enter your Dataverse URL.");

        var scopes = new[] { $"{_orgUrl}/.default" };
        var pca = EnsurePca();

        try
        {
            // Try silent acquisition from MSAL token cache first.
            var accounts = await pca.GetAccountsAsync().ConfigureAwait(false);
            _account = accounts.FirstOrDefault();

            if (_account is not null)
            {
                var silent = await pca
                    .AcquireTokenSilent(scopes, _account)
                    .ExecuteAsync(ct)
                    .ConfigureAwait(false);
                _account = silent.Account;
                return new AuthResult(true, silent.Account.Username, null);
            }

            // No cached account — require an interactive session.
            if (parentHwnd == nint.Zero)
                return new AuthResult(false, null, "No cached session. Please sign in.");

            var interactive = await pca
                .AcquireTokenInteractive(scopes)
                .WithParentActivityOrWindow(parentHwnd)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
            _account = interactive.Account;
            return new AuthResult(true, interactive.Account.Username, null);
        }
        catch (MsalUiRequiredException)
        {
            // Silent failed because the token expired or consent is needed.
            if (parentHwnd == nint.Zero)
                return new AuthResult(false, null, "Session expired. Please sign in again.");

            try
            {
                var interactive = await pca
                    .AcquireTokenInteractive(scopes)
                    .WithParentActivityOrWindow(parentHwnd)
                    .ExecuteAsync(ct)
                    .ConfigureAwait(false);
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
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new AuthResult(false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default)
    {
        var pca = EnsurePca();
        var result = await pca
            .AcquireTokenSilent(scopes, _account)
            .ExecuteAsync(ct)
            .ConfigureAwait(false);
        return result.AccessToken;
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (_account is not null && _pca is not null)
        {
            await _pca.RemoveAsync(_account).ConfigureAwait(false);
            _account = null;
        }
    }
}

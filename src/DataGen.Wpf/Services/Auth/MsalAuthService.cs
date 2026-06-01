using DataGen.Desktop.Services.Settings;
using Microsoft.Identity.Client;

namespace DataGen.Desktop.Services.Auth;

/// <summary>
/// MSAL public-client authentication service. Attempts silent token acquisition first;
/// falls back to interactive login when silent acquisition fails.
/// </summary>
public sealed class MsalAuthService : IAuthService
{
    private readonly IPublicClientApplication _pca;
    private readonly string _orgUrl;
    private IAccount? _account;

    /// <summary>
    /// Initialises the MSAL public client using settings loaded synchronously at startup.
    /// </summary>
    /// <param name="settings">Settings service supplying ClientId, TenantId, and OrgUrl.</param>
    public MsalAuthService(ISettingsService settings)
    {
        // Load synchronously once at startup — settings file is tiny and read-only here.
        var s = settings.LoadAsync().GetAwaiter().GetResult();
        _orgUrl = s.OrgUrl;

        _pca = PublicClientApplicationBuilder
            .Create(s.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{s.TenantId}")
            .WithDefaultRedirectUri()
            .Build();
    }

    /// <inheritdoc />
    public string? CurrentUserDisplayName => _account?.Username;

    /// <inheritdoc />
    public async Task<AuthResult> SignInAsync(nint parentHwnd, CancellationToken ct = default)
    {
        var scopes = new[] { $"{_orgUrl}/.default" };
        try
        {
            // Try silent acquisition from MSAL token cache first.
            var accounts = await _pca.GetAccountsAsync().ConfigureAwait(false);
            _account = accounts.FirstOrDefault();

            if (_account is not null)
            {
                var silent = await _pca
                    .AcquireTokenSilent(scopes, _account)
                    .ExecuteAsync(ct)
                    .ConfigureAwait(false);
                _account = silent.Account;
                return new AuthResult(true, silent.Account.Username, null);
            }

            // No cached account — require an interactive session.
            if (parentHwnd == nint.Zero)
                return new AuthResult(false, null, "No cached session. Please sign in.");

            var interactive = await _pca
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
                var interactive = await _pca
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
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default)
    {
        var result = await _pca
            .AcquireTokenSilent(scopes, _account)
            .ExecuteAsync(ct)
            .ConfigureAwait(false);
        return result.AccessToken;
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (_account is not null)
        {
            await _pca.RemoveAsync(_account).ConfigureAwait(false);
            _account = null;
        }
    }
}

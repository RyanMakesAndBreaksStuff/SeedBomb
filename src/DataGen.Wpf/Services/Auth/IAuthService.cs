using Seedbomb.Services.Connections;

namespace Seedbomb.Services.Auth;

/// <summary>
/// Provides MSAL-based authentication for the desktop application.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Attempts silent sign-in first; falls back to interactive if needed.
    /// Pass <c>nint.Zero</c> for silent-only (no interactive popup).
    /// </summary>
    Task<AuthResult> SignInAsync(nint parentHwnd, CancellationToken ct = default);

    /// <summary>
    /// Authenticates <paramref name="profile"/> without persisting last-used and without
    /// replacing the live MSAL session. Pass <c>nint.Zero</c> for silent-only.
    /// </summary>
    Task<AuthResult> TryConnectAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default);

    /// <summary>Removes the cached account token.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>Acquires a token silently for the given scopes.</summary>
    Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default);

    /// <summary>Display name of the currently signed-in user, or <c>null</c>.</summary>
    string? CurrentUserDisplayName { get; }
}

using SeedBomb.Services.Connections;

namespace SeedBomb.Services.Auth;

/// <summary>
/// Provides MSAL-based authentication for the desktop application.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Signs in to the last-used profile: silent first, interactive if needed.
    /// Pass <c>nint.Zero</c> for silent-only (no interactive popup). Never throws.
    /// </summary>
    Task<AuthResult> SignInAsync(nint parentHwnd, CancellationToken ct = default);

    /// <summary>
    /// Signs in to <paramref name="profile"/>. On success it becomes the live session and the
    /// last-used profile; on failure both stay as they were. Never throws.
    /// </summary>
    Task<AuthResult> SignInAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default);

    /// <summary>
    /// Profile the live session signed in with, or <see langword="null"/> when signed out. The one
    /// owner of which environment is connected: the Dataverse connection and run history read it.
    /// </summary>
    ConnectionProfile? ActiveProfile { get; }

    /// <summary>
    /// Authenticates <paramref name="profile"/> without persisting last-used and without
    /// replacing the live MSAL session. Pass <c>nint.Zero</c> for silent-only.
    /// </summary>
    Task<AuthResult> TryConnectAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default);

    /// <summary>Removes the cached account token.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>Raised after <see cref="SignOutAsync"/> completes.</summary>
    event EventHandler? SignedOut;

    /// <summary>Acquires a token silently for the given scopes.</summary>
    Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default);

    /// <summary>Display name of the currently signed-in user, or <c>null</c>.</summary>
    string? CurrentUserDisplayName { get; }
}
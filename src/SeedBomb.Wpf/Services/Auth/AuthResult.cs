namespace Seedbomb.Services.Auth;

/// <summary>
/// Result returned by <see cref="IAuthService.SignInAsync"/>.
/// </summary>
/// <param name="Succeeded">Whether authentication completed successfully.</param>
/// <param name="DisplayName">The signed-in user's display name, or <c>null</c> on failure.</param>
/// <param name="Error">The error message, or <c>null</c> on success.</param>
public record AuthResult(bool Succeeded, string? DisplayName, string? Error);
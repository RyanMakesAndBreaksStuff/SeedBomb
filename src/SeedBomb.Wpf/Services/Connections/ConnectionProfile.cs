using CommunityToolkit.Mvvm.ComponentModel;

namespace SeedBomb.Services.Connections;

/// <summary>Authentication mechanism for a connection profile.</summary>
public enum AuthType
{
    /// <summary>Interactive OAuth / delegated flow via browser.</summary>
    OAuth,

    /// <summary>Application identity using a client secret.</summary>
    ClientSecret,

    /// <summary>Application identity using a certificate from the CurrentUser store.</summary>
    Certificate,
}

/// <summary>Dataverse environment tier.</summary>
public enum EnvironmentType
{
    /// <summary>Local or personal development environment.</summary>
    Development,

    /// <summary>Shared test environment.</summary>
    Test,

    /// <summary>User-acceptance testing environment.</summary>
    UAT,

    /// <summary>Live production environment.</summary>
    Production,
}

/// <summary>
/// Represents a saved Dataverse connection configuration.
/// Secrets are plaintext in memory; encrypted on disk via <see cref="JsonConnectionProfileService"/>.
/// </summary>
public sealed partial class ConnectionProfile : ObservableObject
{
    /// <summary>Unique identifier for this profile.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name shown in the connection picker.</summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>Dataverse environment URL, e.g. https://org.crm.dynamics.com.</summary>
    [ObservableProperty] private string _environmentUrl = string.Empty;

    /// <summary>Environment tier classification.</summary>
    [ObservableProperty] private EnvironmentType _environmentType;

    /// <summary>Authentication method used for this connection.</summary>
    [ObservableProperty] private AuthType _authType = AuthType.OAuth;

    /// <summary>Azure AD application (client) ID. Used by all auth types.</summary>
    [ObservableProperty] private string _clientId = string.Empty;

    /// <summary>Azure AD tenant ID. Used by all auth types.</summary>
    [ObservableProperty] private string _tenantId = string.Empty;

    /// <summary>Client secret (plaintext). Used by <see cref="AuthType.ClientSecret"/> only.</summary>
    [ObservableProperty] private string? _clientSecret;

    /// <summary>Certificate thumbprint in CurrentUser\My. Used by <see cref="AuthType.Certificate"/> only.</summary>
    [ObservableProperty] private string? _certificateThumbprint;

    /// <summary>
    /// MSAL account (<c>HomeAccountId.Identifier</c>) this profile last signed in as interactively.
    /// <see cref="AuthType.OAuth"/> only; written by sign-in, never by the editor.
    /// </summary>
    [ObservableProperty] private string? _homeAccountId;

    /// <summary>Not persisted. Set by the VM to mark the currently active (last-used) profile.</summary>
    public bool IsLastUsed { get; set; }
}

/// <summary>Provides all <see cref="AuthType"/> values for binding to ComboBox.</summary>
public static class AuthTypeValues
{
    /// <summary>All authentication type options (Connections page editor).</summary>
    public static readonly AuthType[] All =
        [AuthType.OAuth, AuthType.ClientSecret, AuthType.Certificate];
}
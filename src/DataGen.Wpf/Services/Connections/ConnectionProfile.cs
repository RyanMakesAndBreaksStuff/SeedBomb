namespace DataGen.Desktop.Services.Connections;

/// <summary>Authentication mechanism for a connection profile.</summary>
public enum AuthType
{
    /// <summary>Interactive OAuth / delegated flow via browser.</summary>
    OAuth,
    /// <summary>Application identity using a client secret.</summary>
    ClientSecret,
    /// <summary>Resource-owner password credentials (username + password).</summary>
    UserPassword,
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
public sealed class ConnectionProfile
{
    /// <summary>Unique identifier for this profile.</summary>
    public Guid   Id              { get; init; }  = Guid.NewGuid();

    /// <summary>Display name shown in the connection picker.</summary>
    public string Name            { get; set; }   = string.Empty;

    /// <summary>Dataverse environment URL, e.g. https://org.crm.dynamics.com.</summary>
    public string EnvironmentUrl  { get; set; }   = string.Empty;

    /// <summary>Environment tier classification.</summary>
    public EnvironmentType EnvironmentType { get; set; }

    /// <summary>Authentication method used for this connection.</summary>
    public AuthType AuthType { get; set; } = AuthType.OAuth;

    /// <summary>Azure AD application (client) ID. Used by all auth types.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Azure AD tenant ID. Used by all auth types.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Client secret (plaintext). Used by <see cref="AuthType.ClientSecret"/> only.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Username / UPN. Used by <see cref="AuthType.UserPassword"/> only.</summary>
    public string? Username { get; set; }

    /// <summary>Password (plaintext). Used by <see cref="AuthType.UserPassword"/> only.</summary>
    public string? Password { get; set; }

    /// <summary>Not persisted. Set by the VM to mark the currently active (last-used) profile.</summary>
    public bool IsLastUsed { get; set; }
}

/// <summary>Provides all <see cref="AuthType"/> values for binding to ComboBox.</summary>
public static class AuthTypeValues
{
    /// <summary>All authentication type options.</summary>
    public static readonly AuthType[] All = [AuthType.OAuth, AuthType.ClientSecret, AuthType.UserPassword];
}

/// <summary>Provides all <see cref="EnvironmentType"/> values for binding to ComboBox.</summary>
public static class EnvironmentTypeValues
{
    /// <summary>All environment type options.</summary>
    public static readonly EnvironmentType[] All = [EnvironmentType.Development, EnvironmentType.Test, EnvironmentType.UAT, EnvironmentType.Production];
}

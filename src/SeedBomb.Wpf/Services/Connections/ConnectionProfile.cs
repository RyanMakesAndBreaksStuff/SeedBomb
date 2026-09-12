using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Seedbomb.Services.Connections;

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
public sealed class ConnectionProfile : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _environmentUrl = string.Empty;
    private EnvironmentType _environmentType;
    private AuthType _authType = AuthType.OAuth;
    private string _clientId = string.Empty;
    private string _tenantId = string.Empty;
    private string? _clientSecret;
    private string? _certificateThumbprint;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Unique identifier for this profile.</summary>
    public Guid   Id              { get; init; }  = Guid.NewGuid();

    /// <summary>Display name shown in the connection picker.</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Dataverse environment URL, e.g. https://org.crm.dynamics.com.</summary>
    public string EnvironmentUrl { get => _environmentUrl; set => Set(ref _environmentUrl, value); }

    /// <summary>Environment tier classification.</summary>
    public EnvironmentType EnvironmentType { get => _environmentType; set => Set(ref _environmentType, value); }

    /// <summary>Authentication method used for this connection.</summary>
    public AuthType AuthType { get => _authType; set => Set(ref _authType, value); }

    /// <summary>Azure AD application (client) ID. Used by all auth types.</summary>
    public string ClientId { get => _clientId; set => Set(ref _clientId, value); }

    /// <summary>Azure AD tenant ID. Used by all auth types.</summary>
    public string TenantId { get => _tenantId; set => Set(ref _tenantId, value); }

    /// <summary>Client secret (plaintext). Used by <see cref="AuthType.ClientSecret"/> only.</summary>
    public string? ClientSecret { get => _clientSecret; set => Set(ref _clientSecret, value); }

    /// <summary>Certificate thumbprint in CurrentUser\My. Used by <see cref="AuthType.Certificate"/> only.</summary>
    public string? CertificateThumbprint
    {
        get => _certificateThumbprint;
        set => Set(ref _certificateThumbprint, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

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

namespace DataGen.Desktop.Services.Settings;

/// <summary>
/// Persisted application settings.
/// </summary>
/// <param name="OrgUrl">The Dataverse organization URL.</param>
/// <param name="ClientId">The Entra ID application (client) ID.</param>
/// <param name="TenantId">The Entra ID tenant ID.</param>
/// <param name="DefaultRecordCount">Default number of records to generate per entity.</param>
/// <param name="DefaultBatchSize">Default batch size for bulk creation.</param>
/// <param name="DefaultDop">Default degree of parallelism; 0 means auto.</param>
/// <param name="DarkTheme">Whether dark theme is enabled.</param>
/// <param name="ReduceMotion">Whether animations should be suppressed.</param>
public record AppSettings(
    string OrgUrl,
    string ClientId,
    string TenantId,
    int DefaultRecordCount = 10,
    int DefaultBatchSize = 500,
    int DefaultDop = 0,
    bool DarkTheme = false,
    bool ReduceMotion = false)
{
    /// <summary>Returns a default settings instance with empty credentials.</summary>
    public static AppSettings Default => new(
        OrgUrl: string.Empty,
        ClientId: string.Empty,
        TenantId: string.Empty);
}

using SeedBomb.Core.Contracts;

namespace SeedBomb.Services.Settings;

/// <summary>
/// Persisted application settings. Connection details live in connection profiles,
/// not here — see <see cref="Connections.ConnectionProfile"/>.
/// </summary>
/// <param name="DefaultRecordCount">Default number of records to generate per entity.</param>
/// <param name="DefaultBatchSize">Default batch size for bulk creation.</param>
/// <param name="DefaultDop">Default degree of parallelism; 0 means auto.</param>
/// <param name="DarkTheme">Whether dark theme is enabled.</param>
/// <param name="PaletteId">Selected color palette; see <see cref="Theme.DesignThemeManager.AvailablePalettes"/>.</param>
/// <param name="KeepRunSheetOpen">When true, the run sheet stays up until the run finishes.</param>
/// <param name="RunDiagnostics">
/// When true, the Dataverse SDK's own warnings reach the app log (from the next connection) and each
/// run ends with a per-table row count query.
/// </param>
/// <param name="SdkMaxRetryCount">Retries the Dataverse SDK performs on its own before a fault reaches the app. 0 (default) leaves every retry to the app.</param>
/// <param name="SdkRetryPauseSeconds">Base pause between SDK retries, in seconds. Unused when <paramref name="SdkMaxRetryCount"/> is 0.</param>
/// <param name="ConnectionTimeoutMinutes">HTTP timeout for one Dataverse call, in minutes. The SDK default is 4.</param>
public record AppSettings(
    int DefaultRecordCount = GenerationLimits.DefaultRecordCount,
    int DefaultBatchSize = GenerationLimits.DefaultBatchSize,
    int DefaultDop = 0,
    bool DarkTheme = false,
    string PaletteId = Theme.DesignThemeManager.DefaultPaletteId,
    bool KeepRunSheetOpen = true,
    bool RunDiagnostics = false,
    int SdkMaxRetryCount = 0,
    int SdkRetryPauseSeconds = 0,
    int ConnectionTimeoutMinutes = 4)
{
    /// <summary>Returns a default settings instance.</summary>
    public static AppSettings Default => new();

    /// <summary>This instance with every run-size value inside <see cref="GenerationLimits"/>.</summary>
    public AppSettings Clamped() => this with
    {
        DefaultRecordCount = Math.Clamp(DefaultRecordCount, 1, GenerationLimits.MaxRecordCount),
        DefaultBatchSize = Math.Clamp(DefaultBatchSize, 1, GenerationLimits.MaxBatchSize),
        DefaultDop = Math.Clamp(DefaultDop, 0, GenerationLimits.MaxDop),
        SdkMaxRetryCount = Math.Clamp(SdkMaxRetryCount, 0, 10),
        SdkRetryPauseSeconds = Math.Clamp(SdkRetryPauseSeconds, 0, 300),
        ConnectionTimeoutMinutes = Math.Clamp(ConnectionTimeoutMinutes, 1, 30),
    };
}

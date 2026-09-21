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
public record AppSettings(
    int DefaultRecordCount = 10,
    int DefaultBatchSize = 500,
    int DefaultDop = 0,
    bool DarkTheme = false,
    string PaletteId = Theme.DesignThemeManager.DefaultPaletteId,
    bool KeepRunSheetOpen = true)
{
    /// <summary>Returns a default settings instance.</summary>
    public static AppSettings Default => new();
}
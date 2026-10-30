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
public record AppSettings(
    int DefaultRecordCount = GenerationLimits.DefaultRecordCount,
    int DefaultBatchSize = GenerationLimits.DefaultBatchSize,
    int DefaultDop = 0,
    bool DarkTheme = false,
    string PaletteId = Theme.DesignThemeManager.DefaultPaletteId,
    bool KeepRunSheetOpen = true,
    bool RunDiagnostics = false)
{
    /// <summary>Returns a default settings instance.</summary>
    public static AppSettings Default => new();

    /// <summary>This instance with every run-size value inside <see cref="GenerationLimits"/>.</summary>
    public AppSettings Clamped() => this with
    {
        DefaultRecordCount = Math.Clamp(DefaultRecordCount, 1, GenerationLimits.MaxRecordCount),
        DefaultBatchSize = Math.Clamp(DefaultBatchSize, 1, GenerationLimits.MaxBatchSize),
        DefaultDop = Math.Clamp(DefaultDop, 0, GenerationLimits.MaxDop),
    };
}

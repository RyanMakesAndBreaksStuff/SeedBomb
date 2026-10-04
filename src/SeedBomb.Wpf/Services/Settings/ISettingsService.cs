namespace SeedBomb.Services.Settings;

/// <summary>
/// Loads and saves application settings to local storage.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Loads settings from disk, returning <see cref="AppSettings.Default"/> if the file is absent.
    /// </summary>
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    /// <summary>Persists settings to disk.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Set when the stored file could not be read (moved aside) or opened (left in place), so
    /// settings started at defaults. Null when there is nothing to report.
    /// </summary>
    string? LoadWarning => null;
}
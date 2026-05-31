namespace DataGen.Wpf.Services.Settings;

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
}

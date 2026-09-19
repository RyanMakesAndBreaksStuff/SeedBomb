using Seedbomb.Services.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Seedbomb.Services.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to <c>%LOCALAPPDATA%\SeedBomb\settings.json</c>.
/// Thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
                return AppSettings.Default;

            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct).ConfigureAwait(false)
                   ?? AppSettings.Default;
        }
        catch (JsonException)
        {
            return AppSettings.Default;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using var stream = File.Open(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();
}

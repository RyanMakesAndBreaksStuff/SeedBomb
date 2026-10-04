using Microsoft.Extensions.Logging;
using SeedBomb.Services.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SeedBomb.Services.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to <c>%LOCALAPPDATA%\SeedBomb\settings.json</c>.
/// Thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<JsonSettingsService>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <inheritdoc />
    public string? LoadWarning { get; private set; }

    // WR-008: set while the last load couldn't open the file. This instance then holds only defaults,
    // so SaveAsync refuses rather than overwrite the user's settings with them.
    private bool _unreadable;

    /// <summary>Stores settings in <c>%LOCALAPPDATA%\SeedBomb\settings.json</c>.</summary>
    /// <param name="logger">Warns when an unreadable settings file is moved aside.</param>
    public JsonSettingsService(ILogger<JsonSettingsService> logger)
        : this(AppPaths.Root, logger)
    {
    }

    /// <summary>Test seam: stores <c>settings.json</c> in <paramref name="storageDirectory"/>.</summary>
    internal JsonSettingsService(string storageDirectory, ILogger<JsonSettingsService>? logger = null)
    {
        _filePath = Path.Combine(storageDirectory, "settings.json");
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _unreadable = false;
            if (!File.Exists(_filePath))
                return AppSettings.Default;

            await using var stream = File.OpenRead(_filePath);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct).ConfigureAwait(false);
            return loaded?.Clamped() ?? AppSettings.Default;
        }
        catch (JsonException ex)
        {
            // WR-008: keep the unreadable file — the next save would otherwise replace it with defaults.
            var kept = AtomicFile.Quarantine(_filePath);
            LoadWarning = $"Settings could not be read, so SeedBomb started with defaults. The file was kept at {kept}.";
            _logger?.LogWarning(ex, "Settings were unreadable; moved them to {Path} and loaded defaults", kept);
            return AppSettings.Default;
        }
        catch (Exception ex) when (AtomicFile.IsUnavailable(ex))
        {
            _unreadable = true;
            LoadWarning = AtomicFile.UnavailableWarning("Settings", _filePath, ex);
            _logger?.LogWarning(ex, "Settings could not be opened; using defaults and leaving {Path} untouched", _filePath);
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
            if (_unreadable)
                throw new IOException(LoadWarning);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await AtomicFile.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(settings.Clamped(), JsonOptions), ct).ConfigureAwait(false);
            LoadWarning = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();
}

using Microsoft.Extensions.Logging;
using SeedBomb.Services.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SeedBomb.Services.History;

/// <summary>
/// Persists run history to <c>%LOCALAPPDATA%\SeedBomb\history.json</c>.
/// Thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class JsonRunHistoryService : IRunHistoryService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<JsonRunHistoryService>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _loadWarning;

    /// <inheritdoc />
    public string? TakeLoadWarning() => Interlocked.Exchange(ref _loadWarning, null);

    /// <summary>Stores history in <c>%LOCALAPPDATA%\SeedBomb\history.json</c>.</summary>
    /// <param name="logger">Warns when an unreadable history file is moved aside.</param>
    public JsonRunHistoryService(ILogger<JsonRunHistoryService> logger)
        : this(AppPaths.Root, logger)
    {
    }

    /// <summary>Test seam: stores <c>history.json</c> in <paramref name="storageDirectory"/>.</summary>
    internal JsonRunHistoryService(string storageDirectory, ILogger<JsonRunHistoryService>? logger = null)
    {
        _filePath = Path.Combine(storageDirectory, "history.json");
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AddRunAsync(RunRecord run, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await ReadCoreAsync(ct).ConfigureAwait(false);
            list.Insert(0, run);
            await WriteCoreAsync(list, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunRecord>> GetRunsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync([], ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<RunRecord>> ReadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<RunRecord>>(stream, JsonOptions, ct).ConfigureAwait(false)
                   ?? [];
        }
        catch (JsonException ex)
        {
            // WR-008: keep the unreadable file — the next write would otherwise erase every past run.
            var kept = AtomicFile.Quarantine(_filePath);
            _loadWarning = $"Run history could not be read, so SeedBomb started without it. The file was kept at {kept}.";
            _logger?.LogWarning(ex, "Run history was unreadable; moved it to {Path} and started empty", kept);
            return [];
        }
    }

    private async Task WriteCoreAsync(List<RunRecord> list, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await AtomicFile.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(list, JsonOptions), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();
}
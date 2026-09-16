using System.IO;
using System.Text.Json;
using Seedbomb.Services.Diagnostics;

namespace Seedbomb.Services.History;

/// <summary>
/// Persists run history to <c>%LOCALAPPDATA%\SeedBomb\history.json</c>.
/// Thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class JsonRunHistoryService : IRunHistoryService, IDisposable
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "history.json");

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _lock = new(1, 1);

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

    private static async Task<List<RunRecord>> ReadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
            return [];

        try
        {
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<List<RunRecord>>(stream, JsonOptions, ct).ConfigureAwait(false)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task WriteCoreAsync(List<RunRecord> list, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var stream = File.Open(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, list, JsonOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();
}

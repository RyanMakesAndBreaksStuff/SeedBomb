using DataGen.Core.Exceptions;
using DataGen.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.Services.Dataverse;
using System.ServiceModel;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Seedbomb.ViewModels;

/// <summary>Outcome of one metadata fetch generation.</summary>
/// <param name="Generation">Monotonic fetch id used to drop stale completions.</param>
/// <param name="IsCanceled">True when this generation was canceled.</param>
/// <param name="IsStale">True when a newer generation superseded this one.</param>
/// <param name="Entities">Fetched entities on success; otherwise null.</param>
/// <param name="Error">Plain-language failure copy; null when not failed.</param>
/// <param name="Exception">Failure exception; null when not failed.</param>
public readonly record struct MetadataFetchResult(
    int Generation,
    bool IsCanceled,
    bool IsStale,
    IReadOnlyList<EntityMetadata>? Entities,
    string? Error,
    Exception? Exception);

/// <summary>Loads table metadata, caches entities, and owns fetch cancellation.</summary>
public sealed class RuleMetadataLoader
{
    private readonly IMetadataProvider _metadata;
    private readonly IDataverseConnectionService? _connection;
    private readonly Dictionary<string, EntityMetadata> _entities = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private CancellationTokenSource? _cts;

    /// <summary>Creates a loader for live Dataverse metadata.</summary>
    public RuleMetadataLoader(IMetadataProvider metadata, IDataverseConnectionService? connection = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
        _connection = connection;
    }

    /// <summary>True when <paramref name="generation"/> is still the in-flight fetch.</summary>
    public bool IsCurrent(int generation) => generation == _generation;

    /// <summary>Looks up a previously fetched entity.</summary>
    public bool TryGet(string logicalName, out EntityMetadata meta) =>
        _entities.TryGetValue(logicalName, out meta!);

    /// <summary>Drops the cached entity set (connection reset / missing profile).</summary>
    public void ClearEntities() => _entities.Clear();

    /// <summary>Listens for connection resets that must cancel in-flight fetches.</summary>
    public void SubscribeReset(EventHandler handler)
    {
        if (_connection is null)
            return;
        _connection.ConnectionReset -= handler;
        _connection.ConnectionReset += handler;
    }

    /// <summary>Stops listening for connection resets.</summary>
    public void UnsubscribeReset(EventHandler handler)
    {
        if (_connection is null)
            return;
        _connection.ConnectionReset -= handler;
    }

    /// <summary>Bumps the generation and cancels the in-flight fetch.</summary>
    public void Cancel()
    {
        Interlocked.Increment(ref _generation);
        _cts?.Cancel();
    }

    /// <summary>Fetches entities for <paramref name="names"/>; never throws <see cref="OperationCanceledException"/>.</summary>
    public async Task<MetadataFetchResult> FetchAsync(
        string[] names, CancellationToken pageToken, CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _generation);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(pageToken, ct);
        var token = _cts.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var list = await _metadata.GetEntitiesAsync(names, token);
            return StoreIfCurrent(generation, token, list);
        }
        catch (OperationCanceledException)
        {
            return new MetadataFetchResult(generation, true, false, null, null, null);
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return new MetadataFetchResult(generation, false, true, null, null, null);
            return new MetadataFetchResult(generation, false, false, null, DescribeFailure(ex), ex);
        }
    }

    /// <summary>Logs the metadata-fetch failure; the caller owns UI error state.</summary>
    public static void LogFailure(
        ILogger<RuleEditorViewModel>? logger, ISnackbarService? snackbar, Exception ex, string message)
    {
        logger?.LogError(ex, "Failed to load table metadata for the Rules page");
        snackbar?.Show("Couldn't load table metadata", message,
            ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
    }

    /// <summary>Maps a live-fetch exception to the banner/snackbar copy.</summary>
    public static string DescribeFailure(Exception ex) => ex switch
    {
        SchemaException schema => schema.Message,
        InvalidOperationException invalid => invalid.Message,
        FaultException<OrganizationServiceFault> fault =>
            $"Dataverse error {fault.Detail.ErrorCode}: {fault.Detail.Message}",
        FaultException fault => fault.Message,
        _ => ex.Message,
    };

    private MetadataFetchResult StoreIfCurrent(
        int generation, CancellationToken token, IReadOnlyList<EntityMetadata> list)
    {
        if (generation != _generation || token.IsCancellationRequested)
            return new MetadataFetchResult(generation, false, true, null, null, null);

        _entities.Clear();
        foreach (var entity in list)
        {
            if (entity.LogicalName is not null)
                _entities[entity.LogicalName] = entity;
        }

        return new MetadataFetchResult(generation, false, false, list, null, null);
    }
}

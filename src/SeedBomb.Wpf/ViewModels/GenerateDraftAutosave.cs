using Microsoft.Extensions.Logging;
using Seedbomb.Services.Profiles;

namespace Seedbomb.ViewModels;

/// <summary>Debounced draft persist for the Generate wizard. Owns the coalesce CTS.</summary>
internal sealed class GenerateDraftAutosave
{
    private readonly IProfileService _profiles;
    private readonly ILogger<GenerateViewModel> _logger;
    private readonly Func<bool> _hasDraft;
    private readonly Func<string, Profile> _snapshot;

    private CancellationTokenSource? _cts;
    private Task? _task;

    /// <summary>Initialises the autosave collaborator.</summary>
    public GenerateDraftAutosave(
        IProfileService profiles,
        ILogger<GenerateViewModel> logger,
        Func<bool> hasDraft,
        Func<string, Profile> snapshot)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hasDraft);
        ArgumentNullException.ThrowIfNull(snapshot);
        _profiles = profiles;
        _logger = logger;
        _hasDraft = hasDraft;
        _snapshot = snapshot;
    }

    /// <summary>Coalesces a 400ms-debounced persist of the current working set.</summary>
    public void Schedule()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _task = DebouncedSaveAsync(_cts.Token);
    }

    /// <summary>Cancels a pending debounce and awaits the in-flight persist, if any.</summary>
    public async Task CancelPendingAsync()
    {
        _cts?.Cancel();
        if (_task is { } pending)
        {
            try { await pending; }
            catch (OperationCanceledException) { }
        }

        _cts?.Dispose();
        _cts = null;
        _task = null;
    }

    /// <summary>Persists the current snapshot immediately, swallowing store failures.</summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        if (!_hasDraft())
            return;

        try
        {
            var profile = _snapshot("draft");
            await _profiles.SaveDraftAsync(profile, ct);
        }
        catch (OperationCanceledException)
        {
            // coalesced
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Draft autosave failed");
        }
    }

    private async Task DebouncedSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
        }
        catch (OperationCanceledException)
        {
            return; // coalesced by a newer edit
        }

        await PersistAsync(ct);
    }
}

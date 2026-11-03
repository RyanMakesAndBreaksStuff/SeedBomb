namespace SeedBomb.Bulk;

/// <summary>
/// Rows per <c>CreateMultiple</c> for one table. Starts low, grows while calls stay fast, and halves
/// after a client timeout. Thread-safe because parallel workers observe calls concurrently.
/// </summary>
/// <param name="configured">The run's configured batch size; the ceiling.</param>
public sealed class AdaptiveBatchSize(int configured)
{
    /// <summary>First batch size when the configured size is larger.</summary>
    public const int InitialSize = 250;

    /// <summary>A call at or under this duration is fast and grows the next batch.</summary>
    public static readonly TimeSpan FastCall = TimeSpan.FromSeconds(20);

    private const int Floor = 25;
    private readonly object _lock = new();
    private int _current = Math.Max(1, Math.Min(configured, InitialSize));

    /// <summary>Rows the next batch should carry.</summary>
    public int Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Grows by half after a fast call, up to the configured size. A slow call keeps the size.</summary>
    /// <param name="duration">How long the last call took.</param>
    public void ObserveCall(TimeSpan duration)
    {
        if (duration > FastCall) return;
        lock (_lock)
            _current = Math.Min(configured, _current + _current / 2);
    }

    /// <summary>Halves the size after a client timeout, not below the floor or the configured size.</summary>
    public void ObserveTimeout()
    {
        lock (_lock)
            _current = Math.Max(Math.Min(Floor, configured), _current / 2);
    }
}

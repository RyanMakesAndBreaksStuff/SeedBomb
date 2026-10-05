namespace SeedBomb.Services.Auth;

/// <summary>
/// Serializes live-session mutations against an in-flight generation run.
/// One process-wide singleton. Token refresh and the Dataverse connection lock never take it;
/// the lock order is this gate, then the connection lock.
/// </summary>
public sealed class RunSessionGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Waits for exclusive access. The returned lease releases the gate exactly once;
    /// disposing it again is a no-op. The gate itself is never disposed.
    /// </summary>
    /// <param name="ct">Cancels the wait. A cancelled wait does not hold the gate.</param>
    /// <returns>A lease whose disposal lets the next waiter in.</returns>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}

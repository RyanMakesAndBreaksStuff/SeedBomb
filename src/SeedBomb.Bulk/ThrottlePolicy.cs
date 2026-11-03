using SeedBomb.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using System.Diagnostics;
using System.ServiceModel;

namespace SeedBomb.Bulk;

/// <summary>
/// The one retry owner for Dataverse calls. The <c>ServiceClient</c> is built with
/// <c>MaxRetryCount = 0</c>, so every fault reaches this policy on the first attempt. A
/// service-protection fault waits the server's <c>Retry-After</c>; timeouts, network faults,
/// HTTP 429/502/503 and busy endpoints wait an exponential backoff with jitter. Any other fault
/// is terminal.
/// </summary>
public class ThrottlePolicy
{
    // Dataverse service-protection codes: number of requests, execution time, concurrency.
    private static readonly int[] ServiceProtectionCodes = [-2147015902, -2147015903, -2147015898];

    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    private readonly ILogger<ThrottlePolicy> _logger;

    // Run-wide pause deadline as a Stopwatch timestamp; 0 means not paused. Shared by every
    // caller of this instance (one per run), so a Retry-After stops the thundering herd.
    private long _pausedUntil;

    /// <summary>Raised before each retry sleep with the entity and the wait, so a UI can show it.</summary>
    public event Action<string, TimeSpan>? Waiting;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThrottlePolicy"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ThrottlePolicy(ILogger<ThrottlePolicy> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes an operation with retry on transient faults: timeouts, network faults, HTTP
    /// 429/502/503, busy endpoints, and service-protection faults (which wait the server's
    /// <c>Retry-After</c>). Any other fault is terminal and wrapped in <see cref="DataGenerationException"/>.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="entityName">The entity name, for logging.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="DataGenerationException">Thrown when max retries are exceeded.</exception>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string entityName,
        int maxRetries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(ct).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            // On the last attempt a transient fault falls through to DataGenerationException.
            catch (Exception ex) when (attempt < maxRetries && RetryReason(ex) is { } reason)
            {
                var retryAfter = RetryAfterOf(ex);
                var delay = retryAfter ?? ComputeDelay(attempt);
                if (retryAfter is { } serverWait)
                    PauseFor(serverWait);
                _logger.LogWarning(
                    "{Reason} for {Entity} on attempt {Attempt} of {MaxAttempts} after {CallMs:F0}ms ({ExceptionType}: {ExceptionMessage}). Retrying in {Delay:F0}ms.",
                    reason, entityName, attempt + 1, maxRetries + 1,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    ex.GetType().Name, ex.Message, delay.TotalMilliseconds);
                Waiting?.Invoke(entityName, delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                throw new DataGenerationException(
                    $"Batch creation failed for '{entityName}' on attempt {attempt + 1}: {ex.Detail?.Message ?? ex.Message}",
                    ex);
            }
            catch (Exception ex)
            {
                throw new DataGenerationException(
                    $"Batch creation failed for '{entityName}' on attempt {attempt + 1}: {ex.Message}",
                    ex);
            }
        }

        // Required by compiler — logically unreachable because catch blocks always throw on final attempt
        throw new DataGenerationException(
            $"Batch creation for '{entityName}' exhausted {maxRetries} retries with no result.");
    }

    private void PauseFor(TimeSpan wait)
    {
        var until = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        // Only ever extend: a later, longer Retry-After wins; a shorter one never shortens the pause.
        long seen;
        do
        {
            seen = Interlocked.Read(ref _pausedUntil);
            if (until <= seen) return;
        } while (Interlocked.CompareExchange(ref _pausedUntil, until, seen) != seen);
    }

    private async Task WaitIfPausedAsync(CancellationToken ct)
    {
        while (true)
        {
            var until = Interlocked.Read(ref _pausedUntil);
            if (until == 0) return;
            var remaining = TimeSpan.FromSeconds((double)(until - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True for a fault a later resend can clear unchanged: a timeout, a network failure, an
    /// unreachable or busy endpoint, HTTP 429/502/503, or a service-protection fault. Data faults
    /// and other protocol errors are false.
    /// </summary>
    /// <param name="ex">The failure, unwrapped from <see cref="DataGenerationException"/>.</param>
    public static bool IsTransient(Exception? ex) => RetryReason(ex) is not null;

    // The one transient classification: ExecuteAsync retries exactly what IsTransient reports.
    private static string? RetryReason(Exception? ex) => ex switch
    {
        // IN-004: a cancel the caller did not request is a transport timeout (TaskCanceledException
        // from HttpClient); ExecuteAsync rethrows a caller cancel before asking.
        TimeoutException or OperationCanceledException => "Timeout",
        HttpRequestException or IOException => "Network fault",
        // The request never reached Dataverse, so a resend cannot duplicate rows. Checked before
        // ProtocolException and FaultException, which are CommunicationExceptions too.
        EndpointNotFoundException => "Endpoint not found",
        ServerTooBusyException => "Server too busy",
        // Recommendation 1: the SDK no longer retries these; the policy does, honouring Retry-After.
        FaultException<OrganizationServiceFault> f when IsServiceProtection(f) => "Service protection",
        ProtocolException p when IsRateLimited(p) => "HTTP 429",
        ProtocolException p when IsGatewayFault(p) => "HTTP 502/503",
        // ServiceClient wraps socket resets (10054) as CommunicationException over HttpRequestException.
        CommunicationException c when IsWrappedNetworkFault(c) => "Network fault",
        _ => null,
    };

    private static bool IsServiceProtection(FaultException<OrganizationServiceFault> ex) =>
        ex.Detail is { } d && Array.IndexOf(ServiceProtectionCodes, d.ErrorCode) >= 0;

    /// <summary>
    /// The <c>Retry-After</c> hint carried by a Dataverse fault, or null when it carries none.
    /// Reads the detail from any <see cref="FaultException{TDetail}"/> over
    /// <see cref="OrganizationServiceFault"/>; it does not check that the fault is a
    /// service-protection fault, so callers own that classification.
    /// </summary>
    /// <param name="ex">The failure as thrown by the operation.</param>
    public static TimeSpan? RetryAfterOf(Exception? ex) =>
        ex is FaultException<OrganizationServiceFault> { Detail.ErrorDetails: { } details }
        && details.TryGetValue("Retry-After", out var value) && value is TimeSpan wait && wait > TimeSpan.Zero
            ? wait
            : null;

    private static bool IsWrappedNetworkFault(CommunicationException ex) =>
        ex.InnerException is HttpRequestException or IOException;

    // ponytail: matches ServiceClient's own message text. A structured status code never reaches
    // us — ThrowIfResponseIsEmpty formats the code into the message and drops the response.
    private static bool IsRateLimited(ProtocolException ex) =>
        ex.Message.Contains("(429)", StringComparison.Ordinal);

    private static bool IsGatewayFault(ProtocolException ex) =>
        ex.Message.Contains("(502)", StringComparison.Ordinal) || ex.Message.Contains("(503)", StringComparison.Ordinal);

    private static TimeSpan ComputeDelay(int attempt)
    {
        var exponential = BaseDelay * Math.Pow(2, attempt);
        exponential = TimeSpan.FromSeconds(Math.Min(60, exponential.TotalSeconds));
        var jitter = TimeSpan.FromMilliseconds((Random.Shared.NextDouble() * 4000) - 2000);
        return exponential + jitter;
    }
}

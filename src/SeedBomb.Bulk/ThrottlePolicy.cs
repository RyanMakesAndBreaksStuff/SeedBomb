using SeedBomb.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using System.ServiceModel;

namespace SeedBomb.Bulk;

/// <summary>
/// Executes Dataverse API calls with automatic retry on transient timeout and network faults.
/// Service-protection (throttle) faults are not retried here — <c>ServiceClient</c> already
/// pauses and resends those itself before a fault ever surfaces. A bare HTTP 429 is the exception:
/// it reaches us as a <see cref="ProtocolException"/> that <c>ServiceClient</c> did not handle, so
/// this policy resends it. Uses exponential backoff with jitter.
/// </summary>
public class ThrottlePolicy
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    private readonly ILogger<ThrottlePolicy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThrottlePolicy"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ThrottlePolicy(ILogger<ThrottlePolicy> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes an operation with retry on transient timeout and network faults.
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
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            // IN-004: any other cancel is a transport timeout (TaskCanceledException from HttpClient):
            // retry it like a timeout; on the last attempt it falls through to DataGenerationException.
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && attempt < maxRetries)
            {
                var delay = ComputeDelay(attempt);
                _logger.LogWarning(
                    "Timeout for {Entity} on attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms.",
                    entityName, attempt + 1, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                var delay = ComputeDelay(attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < maxRetries)
            {
                var delay = ComputeDelay(attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (ProtocolException ex) when (attempt < maxRetries && IsRateLimited(ex))
            {
                var delay = ComputeDelay(attempt);
                _logger.LogWarning(
                    "HTTP 429 for {Entity} on attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms.",
                    entityName, attempt + 1, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                // ServiceClient already pauses and resends on service-protection limits, so any
                // fault that still surfaces is terminal — wrap it, don't retry it.
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

    /// <summary>
    /// True for a fault a later resend can clear unchanged: a timeout, a network failure, an
    /// unreachable or busy endpoint, or HTTP 429. Service faults and other protocol errors are false.
    /// </summary>
    /// <param name="ex">The failure, unwrapped from <see cref="DataGenerationException"/>.</param>
    public static bool IsTransient(Exception? ex) => ex switch
    {
        TimeoutException or OperationCanceledException or HttpRequestException or IOException => true,
        // The request never reached Dataverse, so a resend cannot duplicate rows. Checked before
        // ProtocolException and FaultException, which are CommunicationExceptions too.
        EndpointNotFoundException or ServerTooBusyException => true,
        ProtocolException p => IsRateLimited(p),
        _ => false,
    };

    // ponytail: matches ServiceClient's own message text. A structured status code never reaches
    // us — ThrowIfResponseIsEmpty formats the code into the message and drops the response.
    private static bool IsRateLimited(ProtocolException ex) =>
        ex.Message.Contains("(429)", StringComparison.Ordinal);

    private static TimeSpan ComputeDelay(int attempt)
    {
        var exponential = BaseDelay * Math.Pow(2, attempt);
        exponential = TimeSpan.FromSeconds(Math.Min(60, exponential.TotalSeconds));
        var jitter = TimeSpan.FromMilliseconds((Random.Shared.NextDouble() * 4000) - 2000);
        return exponential + jitter;
    }
}

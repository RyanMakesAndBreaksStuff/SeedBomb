using DataGen.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using System.ServiceModel;

namespace DataGen.Bulk;

/// <summary>
/// Executes Dataverse API calls with automatic retry on transient throttle and timeout faults.
/// Uses exponential backoff with jitter.
/// </summary>
public class ThrottlePolicy
{
    // Dataverse service protection limit error codes
    private const int ErrorCodeNumberOfRequests = -2147015902;
    private const int ErrorCodeTimeLimitExceeded = -2147015903;
    private const int ErrorCodeConcurrentRequests = -2147015898;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    private readonly ILogger<ThrottlePolicy> _logger;
    private readonly Random _random = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ThrottlePolicy"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ThrottlePolicy(ILogger<ThrottlePolicy> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes an operation with retry on throttle and timeout faults.
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FaultException<OrganizationServiceFault> ex) when (IsThrottleFault(ex) && attempt < maxRetries)
            {
                // Retryable throttle fault on a non-final attempt — back off and retry
                var delay = ComputeDelay(attempt, ex);
                _logger.LogWarning(
                    "Dataverse throttle fault for {Entity} (error {Code}) on attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms.",
                    entityName, ex.Detail?.ErrorCode, attempt + 1, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (TimeoutException) when (attempt < maxRetries)
            {
                var delay = ComputeDelay(attempt, null);
                _logger.LogWarning(
                    "Timeout for {Entity} on attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms.",
                    entityName, attempt + 1, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                // Non-throttle fault OR throttle fault on the final attempt — give up
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

    private static bool IsThrottleFault(FaultException<OrganizationServiceFault> ex)
    {
        if (ex.Detail is null) return false;
        return ex.Detail.ErrorCode is
            ErrorCodeNumberOfRequests or
            ErrorCodeTimeLimitExceeded or
            ErrorCodeConcurrentRequests;
    }

    private TimeSpan ComputeDelay(int attempt, FaultException<OrganizationServiceFault>? ex)
    {
        // Exponential backoff: base * 2^attempt
        var exponential = BaseDelay * Math.Pow(2, attempt);

        // Jitter: up to 1 second of random offset to avoid thundering herd
        var jitter = TimeSpan.FromMilliseconds(_random.NextDouble() * 1000);

        return exponential + jitter;
    }
}

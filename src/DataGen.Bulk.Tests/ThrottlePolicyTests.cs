using Microsoft.Xrm.Sdk.Query;
using System.ServiceModel;

namespace DataGen.Bulk.Tests;

public class ThrottlePolicyTests
{
    private readonly ThrottlePolicy _policy = new(NullLogger<ThrottlePolicy>.Instance);

    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttempt_ReturnsResult()
    {
        var result = await _policy.ExecuteAsync(
            () => Task.FromResult(42),
            "account",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ExecuteAsync_FailsOnceWithThrottleFault_RetriesAndSucceeds()
    {
        int callCount = 0;
        var throttleFault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -2147015902, Message = "Too many requests" },
            "Throttled");

        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1) throw throttleFault;
                return Task.FromResult("success");
            },
            "account",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_ExceedsMaxRetries_ThrowsDataGenerationException()
    {
        var throttleFault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -2147015902, Message = "Too many requests" },
            "Throttled");

        await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<string>(
                () => throw throttleFault,
                "account",
                maxRetries: 1,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NonThrottleFault_RethrowsAfterMaxRetries()
    {
        // A non-throttle fault (e.g. validation error) should NOT be retried
        // but still wraps in DataGenerationException on max retries
        var validationFault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -2147220969, Message = "Attribute validation error" },
            "Validation failed");

        var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<string>(
                () => throw validationFault,
                "account",
                maxRetries: 0,
                CancellationToken.None));

        Assert.Contains("account", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _policy.ExecuteAsync<string>(null!, "account", 3, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullEntityName()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _policy.ExecuteAsync(() => Task.FromResult(1), null!, 3, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_ThrowsOperationCancelledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _policy.ExecuteAsync(
                () => Task.FromResult(1),
                "account",
                maxRetries: 3,
                cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_CalledConcurrently_AllSucceed()
    {
        // Regression: _random = new Random() corrupts under concurrent access.
        // Random.Shared is thread-safe; this test would hang or throw before the fix.
        var results = new System.Collections.Concurrent.ConcurrentBag<int>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 40),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (i, ct) =>
            {
                var result = await _policy.ExecuteAsync(
                    () => Task.FromResult(i), "entity", maxRetries: 0, ct);
                results.Add(result);
            });
        Assert.Equal(40, results.Count);
    }

    [Fact]
    public async Task ExecuteAsync_HonorsRetryAfterHeader_WhenPresent()
    {
        // Arrange — fault with Retry-After: 4 seconds.
        // BaseDelay=2s, attempt=0 → exponential=2s; 4s > 2s so Retry-After wins.
        // Without the fix, max delay would be ~3s (2s + 1s jitter); with it, floor is 4s.
        var fault = new OrganizationServiceFault
        {
            ErrorCode = -2147015902, // NumberOfRequests throttle code
            Message = "Too many requests"
        };
        fault.ErrorDetails["Retry-After"] = 4; // 4 seconds — exceeds exponential floor

        var faultException = new FaultException<OrganizationServiceFault>(
            fault,
            new FaultReason("Too many requests"));

        var callCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await _policy.ExecuteAsync<int>(
            () =>
            {
                callCount++;
                if (callCount == 1) throw faultException;
                return Task.FromResult(42);
            },
            "account",
            maxRetries: 2,
            CancellationToken.None);

        sw.Stop();

        Assert.Equal(42, result);
        Assert.Equal(2, callCount);
        // Retry-After=4s means delay >= 4s; assert >= 3500ms to allow for timer resolution
        Assert.True(sw.Elapsed.TotalMilliseconds >= 3500,
            $"Expected >= 3500ms delay (Retry-After=4s), got {sw.Elapsed.TotalMilliseconds}ms");
    }
}

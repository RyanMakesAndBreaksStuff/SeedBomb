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
    public async Task ExecuteAsync_ThrottleFault_IsNotRetriedByPolicy()
    {
        // ServiceClient already pauses and resends on service-protection codes before a fault
        // ever surfaces, so if one still reaches the policy it is terminal: wrap immediately.
        var throttleFault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -2147015902, Message = "Too many requests" },
            "Throttled");

        var callCount = 0;
        var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw throttleFault;
                },
                "account",
                maxRetries: 3,
                CancellationToken.None));

        Assert.Equal(1, callCount);
        Assert.Same(throttleFault, ex.InnerException);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutFault_RetriesAndSucceeds()
    {
        var callCount = 0;

        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1) throw new TimeoutException("socket timeout");
                return Task.FromResult("success");
            },
            "account",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
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
    public async Task ExecuteAsync_Http429_RetriesAndSucceeds()
    {
        // ServiceClient surfaces a bare HTTP 429 as ProtocolException, not a service-protection
        // fault, so the policy owns the resend. A 429 is rejected before processing: no duplicates.
        var callCount = 0;

        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1)
                    throw new ProtocolException("The remote server returned an unexpected response: (429)  .");
                return Task.FromResult("success");
            },
            "contact",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_NonThrottleProtocolFault_IsNotRetried()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw new ProtocolException("The remote server returned an unexpected response: (502)  .");
                },
                "contact",
                maxRetries: 3,
                CancellationToken.None));

        Assert.Equal(1, callCount);
    }
}

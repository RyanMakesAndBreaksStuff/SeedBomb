using Microsoft.Extensions.Logging;
using Moq;
using System.ServiceModel;

namespace SeedBomb.Bulk.Tests;

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

    private static FaultException<OrganizationServiceFault> ThrottleFault(int code, TimeSpan retryAfter)
    {
        var fault = new OrganizationServiceFault { ErrorCode = code, Message = "Service protection limit." };
        fault.ErrorDetails["Retry-After"] = retryAfter;
        return new FaultException<OrganizationServiceFault>(fault, "Throttled");
    }

    [Theory]
    [InlineData(-2147015902)]
    [InlineData(-2147015903)]
    [InlineData(-2147015898)]
    public async Task ExecuteAsync_ThrottleFaultWithRetryAfter_WaitsOnceAndStaysWithinBudget(int code)
    {
        // Recommendation 1 and 8: the app is the only retry owner, it waits exactly the server's
        // Retry-After, and the attempt count is bounded by maxRetries + 1 whatever the server says.
        var callCount = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw ThrottleFault(code, TimeSpan.FromMilliseconds(300));
                },
                "account",
                maxRetries: 2,
                CancellationToken.None));

        Assert.Equal(3, callCount);
        // Two waits of 300 ms, no exponential 2 s base: well under 2 s in total.
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(2));
        Assert.True(ThrottlePolicy.IsTransient(ex.InnerException));
    }

    [Fact]
    public async Task ExecuteAsync_ThrottleFaultWithoutRetryAfter_UsesBackoff()
    {
        var callCount = 0;
        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1)
                    throw new FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { ErrorCode = -2147015902, Message = "Too many requests" });
                return Task.FromResult("success");
            },
            "account",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_ThrottleWait_IsCancellable()
    {
        using var cts = new CancellationTokenSource();
        var task = _policy.ExecuteAsync<int>(
            () =>
            {
                cts.CancelAfter(100);
                throw ThrottleFault(-2147015902, TimeSpan.FromMinutes(5));
            },
            "account",
            maxRetries: 1,
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // was: ExecuteAsync_ThrottleFault_IsNotRetriedByPolicy, asserting callCount == 1
    [Fact]
    public async Task ExecuteAsync_ThrottleFault_IsRetriedByPolicy()
    {
        // The SDK client is built with MaxRetryCount = 0, so a service-protection fault reaches
        // the policy on the first attempt and the policy owns the resend.
        var throttleFault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -2147015902, Message = "Too many requests" },
            "Throttled");

        var callCount = 0;
        await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw throttleFault;
                },
                "account",
                maxRetries: 1,
                CancellationToken.None));

        Assert.Equal(2, callCount);
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
    public async Task ExecuteAsync_TransportTimeoutCancellation_IsRetried()
    {
        // IN-004: HttpClient reports its own timeout as TaskCanceledException while the caller's
        // token is untouched. That is a transient fault, not a user cancel.
        var callCount = 0;

        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1) throw new TaskCanceledException("HttpClient.Timeout elapsed");
                return Task.FromResult("success");
            },
            "account",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_TransportTimeoutOnLastAttempt_IsAFailureNotACancel()
    {
        await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () => throw new TaskCanceledException("HttpClient.Timeout elapsed"),
                "account",
                maxRetries: 0,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutWithResendDisabled_IsTerminalAndStillTransient()
    {
        // Recommendation 3: a create that timed out may have committed; the caller decides what to
        // do, the policy must not resend it. IsTransient still reports it, so the sheet can label it.
        var callCount = 0;
        var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw new TaskCanceledException("HttpClient.Timeout elapsed");
                },
                "account", maxRetries: 3, CancellationToken.None, resendOnTimeout: false));

        Assert.Equal(1, callCount);
        Assert.IsType<TaskCanceledException>(ex.InnerException);
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
    public async Task ExecuteAsync_ConcurrentCallers_ShareOneRetryAfterPause()
    {
        // Recommendation 4: a Retry-After pauses the whole run, not just the batch that was told.
        var policy = new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance);
        var faulted = new TaskCompletionSource();
        var firstCalls = 0;

        var first = policy.ExecuteAsync(
            () =>
            {
                if (++firstCalls == 1)
                {
                    faulted.SetResult();
                    throw ThrottleFault(-2147015902, TimeSpan.FromMilliseconds(500));
                }
                return Task.FromResult(1);
            },
            "account", maxRetries: 1, CancellationToken.None);

        await faulted.Task;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var second = await policy.ExecuteAsync(() => Task.FromResult(2), "contact", maxRetries: 0, CancellationToken.None);

        Assert.Equal(2, second);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(2));
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task ExecuteAsync_ShorterLaterRetryAfter_DoesNotShortenTheArmedPause()
    {
        // A shorter hint must never pull the deadline in: the pause only ever extends.
        var policy = new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance);
        var go = new TaskCompletionSource();
        var secondCalls = 0;

        // In flight before the pause exists, so its hint lands while other callers are asleep.
        var second = policy.ExecuteAsync<int>(
            async () =>
            {
                if (++secondCalls == 1)
                {
                    await go.Task;
                    throw ThrottleFault(-2147015902, TimeSpan.FromMilliseconds(150));
                }
                return 2;
            },
            "contact", maxRetries: 1, CancellationToken.None);

        var armed = new TaskCompletionSource();
        var firstCalls = 0;

        var first = policy.ExecuteAsync(
            () =>
            {
                if (++firstCalls == 1)
                {
                    armed.SetResult();
                    throw ThrottleFault(-2147015902, TimeSpan.FromMilliseconds(700));
                }
                return Task.FromResult(1);
            },
            "account", maxRetries: 1, CancellationToken.None);

        await armed.Task;
        var started = System.Diagnostics.Stopwatch.StartNew();
        // Starting this caller reads the armed deadline synchronously, so it is asleep on the
        // 700 ms pause before the shorter hint below arrives.
        var third = policy.ExecuteAsync(() => Task.FromResult(3), "lead", maxRetries: 0, CancellationToken.None);

        go.SetResult();

        Assert.Equal(3, await third);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));
        Assert.Equal(2, await second);
        Assert.Equal(2, secondCalls);
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task ExecuteAsync_LongerLaterRetryAfter_ExtendsAnAlreadyWaitingCaller()
    {
        // Every caller shares one deadline: a longer hint arriving mid-wait reaches the callers
        // already asleep on the shorter one.
        var policy = new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance);
        var go = new TaskCompletionSource();
        var secondCalls = 0;

        // In flight before the pause exists, so its hint lands while other callers are asleep.
        var second = policy.ExecuteAsync<int>(
            async () =>
            {
                if (++secondCalls == 1)
                {
                    await go.Task;
                    throw ThrottleFault(-2147015902, TimeSpan.FromMilliseconds(800));
                }
                return 2;
            },
            "contact", maxRetries: 1, CancellationToken.None);

        var armed = new TaskCompletionSource();
        var firstCalls = 0;

        var first = policy.ExecuteAsync(
            () =>
            {
                if (++firstCalls == 1)
                {
                    armed.SetResult();
                    throw ThrottleFault(-2147015902, TimeSpan.FromMilliseconds(300));
                }
                return Task.FromResult(1);
            },
            "account", maxRetries: 1, CancellationToken.None);

        await armed.Task;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var third = policy.ExecuteAsync(() => Task.FromResult(3), "lead", maxRetries: 0, CancellationToken.None);

        // The longer hint lands while the caller above is asleep on the 300 ms deadline.
        go.SetResult();

        Assert.Equal(3, await third);
        // Past the original 300 ms deadline, not just the extended 800 ms one.
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(550), TimeSpan.FromSeconds(3));
        Assert.Equal(2, await second);
        Assert.Equal(2, secondCalls);
        Assert.Equal(1, await first);
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

    // was: ExecuteAsync_NonThrottleProtocolFault_IsNotRetried, asserting callCount == 1 on (502)
    [Theory]
    [InlineData("(502)")]
    [InlineData("(503)")]
    public async Task ExecuteAsync_GatewayProtocolFault_IsRetried(string status)
    {
        var callCount = 0;
        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1)
                    throw new ProtocolException($"The remote server returned an unexpected response: {status}  .");
                return Task.FromResult("success");
            },
            "contact",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_ConnectionResetWrappedInCommunicationException_IsRetried()
    {
        // ServiceClient surfaces a socket reset (10054) mid-upload as CommunicationException
        // wrapping HttpRequestException, not the bare HttpRequestException.
        var callCount = 0;

        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1)
                    throw new CommunicationException(
                        "Error while copying content to a stream.",
                        new HttpRequestException("Error while copying content to a stream.",
                            new IOException("Unable to write data to the transport connection.")));
                return Task.FromResult("success");
            },
            "account",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }

    public static TheoryData<Exception> UnreachedEndpointCases => new()
    {
        new EndpointNotFoundException("There was no endpoint listening at https://contoso.crm.dynamics.com."),
        new ServerTooBusyException("The server is too busy."),
    };

    [Theory]
    [MemberData(nameof(UnreachedEndpointCases))]
    public async Task ExecuteAsync_FaultIsTransientReports_IsRetried(Exception fault)
    {
        // IsTransient and ExecuteAsync share one classification, so nothing IsTransient calls
        // transient is left to fail on the first attempt.
        var callCount = 0;

        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1) throw fault;
                return Task.FromResult("success");
            },
            "account",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_DirectHttpRequestException_LogsTheRetry()
    {
        var logger = new Mock<ILogger<ThrottlePolicy>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var policy = new ThrottlePolicy(logger.Object);
        var callCount = 0;

        await policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1) throw new HttpRequestException("An existing connection was forcibly closed.");
                return Task.FromResult(1);
            },
            "account",
            maxRetries: 3,
            CancellationToken.None);

        logger.Verify(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Network fault for account on attempt 1 of 4")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    public static TheoryData<Exception, bool> TransientCases => new()
    {
        { new CommunicationException("Error while copying content to a stream.", new HttpRequestException()), true },
        { new CommunicationException("Unrecognized message version."), false },
        { new EndpointNotFoundException("There was no endpoint listening at https://contoso.crm.dynamics.com."), true },
        { new ServerTooBusyException("The server is too busy."), true },
        { new TimeoutException(), true },
        { new HttpRequestException(), true },
        { new ProtocolException("The remote server returned an unexpected response: (429)  ."), true },
        { new ProtocolException("The remote server returned an unexpected response: (502)  ."), true },
        { new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -2147015902 }), true },
        { new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -2147220969 }), false },
        { new InvalidOperationException(), false },
    };

    [Theory]
    [MemberData(nameof(TransientCases))]
    public void IsTransient_SeparatesNetworkFaultsFromDataFaults(Exception ex, bool expected) =>
        Assert.Equal(expected, ThrottlePolicy.IsTransient(ex));
}

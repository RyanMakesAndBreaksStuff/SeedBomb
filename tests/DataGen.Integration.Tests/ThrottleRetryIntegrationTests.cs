using System.ServiceModel;
using DataGen.Core.Exceptions;

namespace DataGen.Integration.Tests;

public class ThrottleRetryIntegrationTests
{
    // Dataverse service protection limit error codes
    private const int ErrorCodeNumberOfRequests = -2147015902;

    private static FaultException<OrganizationServiceFault> MakeThrottleFault()
    {
        var fault = new OrganizationServiceFault();
        fault.ErrorCode = ErrorCodeNumberOfRequests;
        fault.Message = "Number of requests exceeded the limit.";
        return new FaultException<OrganizationServiceFault>(fault, new FaultReason(fault.Message));
    }

    private static ThrottlePolicy MakeThrottlePolicy()
        => new(NullLogger<ThrottlePolicy>.Instance);

    [Fact]
    public async Task CreateAsync_SingleThrottleError_RetriesAndSucceeds()
    {
        var callCount = 0;
        var throttlePolicy = MakeThrottlePolicy();

        // First call throws a throttle fault; second call succeeds
        async Task<int> Operation()
        {
            await Task.Yield();
            if (callCount++ == 0)
                throw MakeThrottleFault();
            return 42;
        }

        var result = await throttlePolicy.ExecuteAsync(Operation, "account", maxRetries: 3, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task CreateAsync_ExceedsMaxRetries_ThrowsOrRecordsError()
    {
        var throttlePolicy = MakeThrottlePolicy();

        // Always throws throttle faults
        Task<int> AlwaysThrottle()
            => throw MakeThrottleFault();

        // With maxRetries = 1: attempt 0 retries once (attempt 1 is the final attempt)
        // On the final attempt the ThrottlePolicy re-throws as DataGenerationException
        var ex = await Assert.ThrowsAsync<DataGenerationException>(
            () => throttlePolicy.ExecuteAsync(AlwaysThrottle, "account", maxRetries: 1, CancellationToken.None));

        Assert.Contains("account", ex.Message);
    }
}

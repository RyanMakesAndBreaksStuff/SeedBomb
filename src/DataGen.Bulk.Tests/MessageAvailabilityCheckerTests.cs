using Microsoft.Xrm.Sdk.Query;

namespace DataGen.Bulk.Tests;

public class MessageAvailabilityCheckerTests
{
    private static Mock<IOrganizationServiceAsync2> MakeServiceMock(bool hasRecords)
    {
        var mock = new Mock<IOrganizationServiceAsync2>();
        var entityCollection = new EntityCollection(
            hasRecords ? [new Entity("sdkmessagefilter")] : []);
        mock.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityCollection);
        return mock;
    }

    [Fact]
    public async Task IsCreateMultipleAvailableAsync_ServiceReturnsFiler_ReturnsTrue()
    {
        var checker = new MessageAvailabilityChecker(
            MakeServiceMock(hasRecords: true).Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        var result = await checker.IsCreateMultipleAvailableAsync("new_widget");

        Assert.True(result);
    }

    [Fact]
    public async Task IsCreateMultipleAvailableAsync_ServiceReturnsEmpty_ReturnsFalse()
    {
        var checker = new MessageAvailabilityChecker(
            MakeServiceMock(hasRecords: false).Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        var result = await checker.IsCreateMultipleAvailableAsync("account");

        Assert.False(result);
    }

    [Fact]
    public async Task IsCreateMultipleAvailableAsync_CachesResult_QueriesOnlyOnce()
    {
        var mock = MakeServiceMock(hasRecords: true);
        var checker = new MessageAvailabilityChecker(
            mock.Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        await checker.IsCreateMultipleAvailableAsync("new_widget");
        await checker.IsCreateMultipleAvailableAsync("new_widget");

        // Service should only be called once (result is cached)
        mock.Verify(s => s.RetrieveMultipleAsync(
            It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsCreateMultipleAvailableAsync_ServiceThrows_DefaultsFalse()
    {
        var mock = new Mock<IOrganizationServiceAsync2>();
        mock.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Service unavailable"));

        var checker = new MessageAvailabilityChecker(
            mock.Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        var result = await checker.IsCreateMultipleAvailableAsync("account");

        Assert.False(result); // Falls back to ExecuteMultiple-safe default
    }

    [Fact]
    public async Task IsCreateMultipleAvailableAsync_ThrowsOnNullEntityName()
    {
        var checker = new MessageAvailabilityChecker(
            MakeServiceMock(true).Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            checker.IsCreateMultipleAvailableAsync(null!));
    }
}

using Microsoft.Xrm.Sdk.Query;

namespace SeedBomb.Bulk.Tests;

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
    public async Task IsUpdateMultipleAvailableAsync_ServiceReturnsFilter_ReturnsTrue()
    {
        var checker = new MessageAvailabilityChecker(
            MakeServiceMock(hasRecords: true).Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        var result = await checker.IsUpdateMultipleAvailableAsync("new_widget");

        Assert.True(result);
    }

    [Fact]
    public async Task IsUpdateMultipleAvailableAsync_ServiceReturnsEmpty_ReturnsFalse()
    {
        var checker = new MessageAvailabilityChecker(
            MakeServiceMock(hasRecords: false).Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        var result = await checker.IsUpdateMultipleAvailableAsync("account");

        Assert.False(result);
    }

    [Fact]
    public async Task IsUpdateMultipleAvailableAsync_CachesResult_QueriesOnlyOnce()
    {
        var mock = MakeServiceMock(hasRecords: true);
        var checker = new MessageAvailabilityChecker(
            mock.Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        await checker.IsUpdateMultipleAvailableAsync("new_widget");
        await checker.IsUpdateMultipleAvailableAsync("new_widget");

        // Service should only be called once (result is cached)
        mock.Verify(s => s.RetrieveMultipleAsync(
            It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsUpdateMultipleAvailableAsync_ThrowsOnNullEntityName()
    {
        var checker = new MessageAvailabilityChecker(
            MakeServiceMock(true).Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            checker.IsUpdateMultipleAvailableAsync(null!));
    }

    [Fact]
    public async Task IsUpdateMultipleAvailableAsync_UsesStringLogicalNameInCondition()
    {
        QueryExpression? capturedQuery = null;
        var mock = new Mock<IOrganizationServiceAsync2>();
        mock.Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => capturedQuery = (QueryExpression)q)
            .ReturnsAsync(new EntityCollection());

        var checker = new MessageAvailabilityChecker(
            mock.Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        await checker.IsUpdateMultipleAvailableAsync("account", CancellationToken.None);

        Assert.NotNull(capturedQuery);
        var cond = capturedQuery.Criteria.Conditions[0];
        Assert.Equal("primaryobjecttypecode", cond.AttributeName);
        Assert.Equal("account", cond.Values[0]); // Must be string logical name, not integer OTC
    }

    [Fact]
    public void ShouldAttemptCreateMultiple_StartsTrueAndNeverProbesService()
    {
        var mock = MakeServiceMock(hasRecords: false);
        var checker = new MessageAvailabilityChecker(
            mock.Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        var result = checker.ShouldAttemptCreateMultiple("account");

        Assert.True(result); // Optimistic: the runtime rejection in BulkCreator is the authority
        mock.Verify(s => s.RetrieveMultipleAsync(
            It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void MarkUnsupported_FlipsShouldAttemptCreateMultipleForThatEntityOnly()
    {
        var checker = new MessageAvailabilityChecker(
            MakeServiceMock(hasRecords: true).Object,
            NullLogger<MessageAvailabilityChecker>.Instance);

        checker.MarkUnsupported("account");

        Assert.False(checker.ShouldAttemptCreateMultiple("account"));
        Assert.True(checker.ShouldAttemptCreateMultiple("contact"));
    }
}

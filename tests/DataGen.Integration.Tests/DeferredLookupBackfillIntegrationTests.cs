using System.ServiceModel;
using DataGen.Core.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Moq;

namespace DataGen.Integration.Tests;

public class DeferredLookupBackfillIntegrationTests
{
    private static DeferredLookupBackfill MakeBackfill(Mock<IOrganizationServiceAsync2> serviceMock)
    {
        var checker = new MessageAvailabilityChecker(serviceMock.Object, NullLogger<MessageAvailabilityChecker>.Instance);
        var throttle = new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance);
        return new DeferredLookupBackfill(serviceMock.Object, checker, throttle, NullLogger<DeferredLookupBackfill>.Instance);
    }

    private static Mock<IOrganizationServiceAsync2> MakeSuccessfulServiceMock()
    {
        var mock = new Mock<IOrganizationServiceAsync2>();

        mock.Setup(s => s.ExecuteAsync(
                It.Is<ExecuteMultipleRequest>(_ => true),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var response = new ExecuteMultipleResponse();
                response.Results["Responses"] = new ExecuteMultipleResponseItemCollection();
                response.Results["IsFaulted"] = false;
                return response;
            });

        return mock;
    }

    [Fact]
    public async Task BackfillLookupsAsync_WithDeferredEdge_UpdatesLookupField()
    {
        var serviceMock = MakeSuccessfulServiceMock();
        var backfill = MakeBackfill(serviceMock);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");

        var deferred = new DeferredLookup("contact", "accountid", new[] { "account" });
        graph.DeferEdge("contact", deferred);

        var pool = new DataverseRecordPool();
        var contactId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        pool.Add("contact", new[] { contactId });
        pool.Add("account", new[] { accountId });

        var errors = await backfill.BackfillLookupsAsync(graph, pool, batchSize: 10);

        Assert.Empty(errors);
        serviceMock.Verify(s => s.ExecuteAsync(
            It.Is<ExecuteMultipleRequest>(r =>
                r.Requests.Count == 1 &&
                r.Requests[0] is UpdateRequest &&
                ((UpdateRequest)r.Requests[0]).Target.LogicalName == "contact" &&
                ((UpdateRequest)r.Requests[0]).Target.Id == contactId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BackfillLookupsAsync_EmptyPool_ProducesNoErrors()
    {
        var serviceMock = MakeSuccessfulServiceMock();
        var backfill = MakeBackfill(serviceMock);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");

        var deferred = new DeferredLookup("contact", "accountid", new[] { "account" });
        graph.DeferEdge("contact", deferred);

        // Pool has no records — backfill should skip gracefully
        var pool = new DataverseRecordPool();

        var errors = await backfill.BackfillLookupsAsync(graph, pool, batchSize: 10);

        Assert.Empty(errors);
        // No ExecuteMultiple calls because there are no source records to update
        serviceMock.Verify(s => s.ExecuteAsync(
            It.IsAny<OrganizationRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}

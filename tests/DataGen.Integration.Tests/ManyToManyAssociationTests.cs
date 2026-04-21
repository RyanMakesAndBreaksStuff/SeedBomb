namespace DataGen.Integration.Tests;

public class ManyToManyAssociationTests
{
    private static DeferredLookupBackfill MakeBackfill(Mock<IOrganizationServiceAsync2> serviceMock)
        => new(serviceMock.Object, NullLogger<DeferredLookupBackfill>.Instance);

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
    public async Task AssociateManyToManyAsync_WithTwoEntities_SendsAssociation()
    {
        var serviceMock = MakeSuccessfulServiceMock();
        var backfill = MakeBackfill(serviceMock);

        var graph = new DependencyGraph();
        var rel = new ManyToManyRelationship("new_account_contact", "account", "contact");
        graph.AddRelationship(rel);

        var pool = new DataverseRecordPool();
        var accountId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        pool.Add("account", [accountId]);
        pool.Add("contact", [contactId]);

        var errors = await backfill.AssociateManyToManyAsync(graph, pool, batchSize: 10);

        Assert.Empty(errors);
        serviceMock.Verify(s => s.ExecuteAsync(
            It.Is<ExecuteMultipleRequest>(r =>
                r.Requests.Count == 1 &&
                r.Requests[0] is AssociateRequest ar &&
                ar.Target.LogicalName == "account" &&
                ar.Target.Id == accountId &&
                ar.Relationship.SchemaName == "new_account_contact"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssociateManyToManyAsync_RelationshipProcessedOnce_NoDuplicates()
    {
        // The relationship is added under both entity keys in the graph (account + contact),
        // but AssociateManyToManyAsync should process each unique SchemaName exactly once.
        var serviceMock = MakeSuccessfulServiceMock();
        var backfill = MakeBackfill(serviceMock);

        var graph = new DependencyGraph();
        var rel = new ManyToManyRelationship("new_account_contact", "account", "contact");
        // AddRelationship adds the relationship under both entity keys automatically
        graph.AddRelationship(rel);

        var pool = new DataverseRecordPool();
        pool.Add("account", [Guid.NewGuid()]);
        pool.Add("contact", [Guid.NewGuid()]);

        var errors = await backfill.AssociateManyToManyAsync(graph, pool, batchSize: 10);

        Assert.Empty(errors);
        // Should only see 1 batch call for 1 account record, even though the relationship
        // is indexed under both "account" and "contact" in the graph.
        serviceMock.Verify(s => s.ExecuteAsync(
            It.Is<ExecuteMultipleRequest>(r => r.Requests.Count == 1 && r.Requests[0] is AssociateRequest),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

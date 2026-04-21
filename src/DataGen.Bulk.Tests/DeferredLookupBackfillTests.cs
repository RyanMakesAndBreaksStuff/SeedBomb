using DataGen.Core.Generators;
using DataGen.Core.Graph;

namespace DataGen.Bulk.Tests;

public class DeferredLookupBackfillTests
{
    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a service mock that returns a valid (unfaulted) ExecuteMultipleResponse and
    /// captures every ExecuteMultipleRequest passed to ExecuteAsync into <paramref name="captured"/>.
    /// </summary>
    private static Mock<IOrganizationServiceAsync2> BuildServiceMock(
        List<ExecuteMultipleRequest> captured)
    {
        var mock = new Mock<IOrganizationServiceAsync2>();
        var successResponse = new ExecuteMultipleResponse
        {
            Results = new ParameterCollection
            {
                ["Responses"] = new ExecuteMultipleResponseItemCollection()
            }
        };

        mock
            .Setup(s => s.ExecuteAsync(It.IsAny<ExecuteMultipleRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is ExecuteMultipleRequest emr)
                    captured.Add(emr);
            })
            .ReturnsAsync(successResponse);

        return mock;
    }

    private static DeferredLookupBackfill BuildSut(IOrganizationServiceAsync2 service) =>
        new DeferredLookupBackfill(service, NullLogger<DeferredLookupBackfill>.Instance);

    // ─── constructor guard tests ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DeferredLookupBackfill(null!, NullLogger<DeferredLookupBackfill>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var mock = new Mock<IOrganizationServiceAsync2>();
        Assert.Throws<ArgumentNullException>(() =>
            new DeferredLookupBackfill(mock.Object, null!));
    }

    // ─── BackfillLookupsAsync – null guards ───────────────────────────────────

    [Fact]
    public async Task BackfillLookupsAsync_NullGraph_ThrowsArgumentNullException()
    {
        var sut = BuildSut(new Mock<IOrganizationServiceAsync2>().Object);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.BackfillLookupsAsync(null!, new DataverseRecordPool(), batchSize: 50));
    }

    [Fact]
    public async Task BackfillLookupsAsync_NullPool_ThrowsArgumentNullException()
    {
        var sut = BuildSut(new Mock<IOrganizationServiceAsync2>().Object);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.BackfillLookupsAsync(new DependencyGraph(), null!, batchSize: 50));
    }

    // ─── BackfillLookupsAsync – behavior ──────────────────────────────────────

    [Fact]
    public async Task BackfillLookupsAsync_NoDeferredEdges_ReturnsEmpty_NoServiceCalls()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var pool = new DataverseRecordPool();
        pool.Add("account", [Guid.NewGuid()]);

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 50);

        Assert.Empty(errors);
        Assert.Empty(captured);
        serviceMock.Verify(
            s => s.ExecuteAsync(It.IsAny<ExecuteMultipleRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BackfillLookupsAsync_WithDeferredEdge_CallsExecuteMultipleWithUpdateRequests()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        // contact → account via parentcustomerid (deferred edge from cycle-breaking)
        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "parentcustomerid", ["account"]));

        var contactId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        var pool = new DataverseRecordPool();
        pool.Add("contact", [contactId]);
        pool.Add("account", [accountId]);

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 50);

        Assert.Empty(errors);
        Assert.NotEmpty(captured);

        var request = captured[0];
        Assert.True(request.Requests.Count > 0);
        var updateRequest = Assert.IsType<UpdateRequest>(request.Requests[0]);
        Assert.IsType<EntityReference>(updateRequest.Target["parentcustomerid"]);
    }

    [Fact]
    public async Task BackfillLookupsAsync_SourceEntityNotInPool_SkipsEntity()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "parentcustomerid", ["account"]));

        // contact has no records in pool
        var pool = new DataverseRecordPool();
        pool.Add("account", [Guid.NewGuid()]);

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 50);

        Assert.Empty(errors);
        Assert.Empty(captured);
        serviceMock.Verify(
            s => s.ExecuteAsync(It.IsAny<ExecuteMultipleRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BackfillLookupsAsync_CancellationToken_ThrowsOperationCancelled()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "parentcustomerid", ["account"]));

        var pool = new DataverseRecordPool();
        pool.Add("contact", [Guid.NewGuid()]);
        pool.Add("account", [Guid.NewGuid()]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.BackfillLookupsAsync(graph, pool, batchSize: 50, ct: cts.Token));
    }

    // ─── AssociateManyToManyAsync – null guards ───────────────────────────────

    [Fact]
    public async Task AssociateManyToManyAsync_NullGraph_ThrowsArgumentNullException()
    {
        var sut = BuildSut(new Mock<IOrganizationServiceAsync2>().Object);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.AssociateManyToManyAsync(null!, new DataverseRecordPool(), batchSize: 50));
    }

    [Fact]
    public async Task AssociateManyToManyAsync_NullPool_ThrowsArgumentNullException()
    {
        var sut = BuildSut(new Mock<IOrganizationServiceAsync2>().Object);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.AssociateManyToManyAsync(new DependencyGraph(), null!, batchSize: 50));
    }

    // ─── AssociateManyToManyAsync – behavior ──────────────────────────────────

    [Fact]
    public async Task AssociateManyToManyAsync_WithRelationship_CallsAssociateRequest()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var rel = new ManyToManyRelationship("new_account_contact", "account", "contact");
        var graph = new DependencyGraph();
        graph.AddNode("account");
        graph.AddNode("contact");
        graph.AddRelationship(rel);

        var pool = new DataverseRecordPool();
        pool.Add("account", [Guid.NewGuid()]);
        pool.Add("contact", [Guid.NewGuid()]);

        var errors = await sut.AssociateManyToManyAsync(graph, pool, batchSize: 50);

        Assert.Empty(errors);
        Assert.NotEmpty(captured);

        var request = captured[0];
        Assert.True(request.Requests.Count > 0);
        Assert.IsType<AssociateRequest>(request.Requests[0]);
    }

    [Fact]
    public async Task AssociateManyToManyAsync_RelationshipProcessedOnce_NoDuplicates()
    {
        // AddRelationship stores rel under both entity1 and entity2; it should only be processed once
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var rel = new ManyToManyRelationship("new_account_contact", "account", "contact");
        var graph = new DependencyGraph();
        graph.AddNode("account");
        graph.AddNode("contact");
        graph.AddRelationship(rel);

        // Confirm that AddRelationship stored it under both entity keys
        Assert.True(graph.Relationships.ContainsKey("account"));
        Assert.True(graph.Relationships.ContainsKey("contact"));

        var pool = new DataverseRecordPool();
        pool.Add("account", [Guid.NewGuid()]);
        pool.Add("contact", [Guid.NewGuid()]);

        await sut.AssociateManyToManyAsync(graph, pool, batchSize: 50);

        // Even though rel is listed under both entity keys, ExecuteAsync must only be called once
        Assert.Single(captured);
    }

    [Fact]
    public async Task AssociateManyToManyAsync_EmptyPool_SkipsRelationship()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var rel = new ManyToManyRelationship("new_account_contact", "account", "contact");
        var graph = new DependencyGraph();
        graph.AddNode("account");
        graph.AddNode("contact");
        graph.AddRelationship(rel);

        // contact has no records in pool
        var pool = new DataverseRecordPool();
        pool.Add("account", [Guid.NewGuid()]);

        var errors = await sut.AssociateManyToManyAsync(graph, pool, batchSize: 50);

        Assert.Empty(errors);
        Assert.Empty(captured);
        serviceMock.Verify(
            s => s.ExecuteAsync(It.IsAny<ExecuteMultipleRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

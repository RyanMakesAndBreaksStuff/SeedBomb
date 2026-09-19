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
        new DeferredLookupBackfill(
            service,
            new MessageAvailabilityChecker(service, NullLogger<MessageAvailabilityChecker>.Instance),
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
            NullLogger<DeferredLookupBackfill>.Instance);

    // ─── constructor guard tests ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DeferredLookupBackfill(
                null!,
                new MessageAvailabilityChecker(new Mock<IOrganizationServiceAsync2>().Object, NullLogger<MessageAvailabilityChecker>.Instance),
                new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
                NullLogger<DeferredLookupBackfill>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var mock = new Mock<IOrganizationServiceAsync2>();
        Assert.Throws<ArgumentNullException>(() =>
            new DeferredLookupBackfill(
                mock.Object,
                new MessageAvailabilityChecker(mock.Object, NullLogger<MessageAvailabilityChecker>.Instance),
                new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
                null!));
    }

    [Fact]
    public void Constructor_NullThrottlePolicy_ThrowsArgumentNullException()
    {
        var mock = new Mock<IOrganizationServiceAsync2>();
        Assert.Throws<ArgumentNullException>(() =>
            new DeferredLookupBackfill(
                mock.Object,
                new MessageAvailabilityChecker(mock.Object, NullLogger<MessageAvailabilityChecker>.Instance),
                null!,
                NullLogger<DeferredLookupBackfill>.Instance));
    }

    [Fact]
    public void Constructor_NullMessageChecker_ThrowsArgumentNullException()
    {
        var mock = new Mock<IOrganizationServiceAsync2>();
        Assert.Throws<ArgumentNullException>(() =>
            new DeferredLookupBackfill(
                mock.Object,
                null!,
                new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
                NullLogger<DeferredLookupBackfill>.Instance));
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
        Assert.Single(request.Requests);
        var updateRequest = Assert.IsType<UpdateRequest>(request.Requests[0]);
        var eref = Assert.IsType<EntityReference>(updateRequest.Target["parentcustomerid"]);
        Assert.Equal("account", eref.LogicalName);
        Assert.Equal(accountId, eref.Id);
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
        Assert.Single(request.Requests);
        var assoc = Assert.IsType<AssociateRequest>(request.Requests[0]);
        Assert.Equal("account", assoc.Target.LogicalName);
        Assert.Equal("new_account_contact", assoc.Relationship.SchemaName);
        Assert.Single(assoc.RelatedEntities);
        Assert.Equal("contact", assoc.RelatedEntities[0].LogicalName);
    }

    [Fact]
    public async Task AssociateManyToManyAsync_RelationshipProcessedOnce_NoDuplicates()
    {
        // AddRelationship stores each rel under both entity1 and entity2; each schema name should only be processed once
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("account");
        graph.AddNode("contact");
        // Two distinct relationships between the same entities
        graph.AddRelationship(new ManyToManyRelationship("new_account_contact", "account", "contact"));
        graph.AddRelationship(new ManyToManyRelationship("new_account_contact2", "account", "contact"));

        // Confirm that AddRelationship stored both under both entity keys
        Assert.True(graph.Relationships.ContainsKey("account"));
        Assert.True(graph.Relationships.ContainsKey("contact"));

        var pool = new DataverseRecordPool();
        pool.Add("account", [Guid.NewGuid()]);
        pool.Add("contact", [Guid.NewGuid()]);

        await sut.AssociateManyToManyAsync(graph, pool, batchSize: 50);

        // 2 distinct schema names → exactly 2 batches (NOT 4, which would indicate dedup failed)
        Assert.Equal(2, captured.Count);
    }

    // ─── BackfillLookupsAsync – error paths ──────────────────────────────────

    [Fact]
    public async Task BackfillLookupsAsync_FaultedResponse_ReturnsBatchErrors()
    {
        var mock = new Mock<IOrganizationServiceAsync2>();

        var faultedResponse = new ExecuteMultipleResponse();
        faultedResponse["IsFaulted"] = true;
        faultedResponse["Responses"] = new ExecuteMultipleResponseItemCollection
        {
            new ExecuteMultipleResponseItem
            {
                Fault = new OrganizationServiceFault { Message = "Test fault", ErrorCode = -1 }
            }
        };

        mock
            .Setup(s => s.ExecuteAsync(It.IsAny<ExecuteMultipleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(faultedResponse);

        var sut = BuildSut(mock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "parentcustomerid", ["account"]));

        var pool = new DataverseRecordPool();
        pool.Add("contact", [Guid.NewGuid()]);
        pool.Add("account", [Guid.NewGuid()]);

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 50);

        Assert.NotEmpty(errors);
        Assert.Equal(-1, errors[0].FaultCode);
    }

    [Fact]
    public async Task BackfillLookupsAsync_ServiceThrows_ReturnsBatchErrors()
    {
        var mock = new Mock<IOrganizationServiceAsync2>();

        mock
            .Setup(s => s.ExecuteAsync(It.IsAny<ExecuteMultipleRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var sut = BuildSut(mock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "parentcustomerid", ["account"]));

        var pool = new DataverseRecordPool();
        pool.Add("contact", [Guid.NewGuid()]);
        pool.Add("account", [Guid.NewGuid()]);

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 50);

        Assert.Single(errors);
        Assert.Contains("test error", errors[0].ErrorMessage);
        Assert.Null(errors[0].FaultCode);
    }

    [Fact]
    public async Task BackfillLookupsAsync_TransientTimeoutOnFirstAttempt_RetriesAndSucceeds()
    {
        int callCount = 0;

        var mock = new Mock<IOrganizationServiceAsync2>();
        mock.Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // Timeout is the transient fault ThrottlePolicy still retries; throttle faults
                // are ServiceClient's job and are terminal by the time they surface here.
                if (Interlocked.Increment(ref callCount) == 1) throw new TimeoutException("socket timeout");
                return Task.FromResult<OrganizationResponse>(new ExecuteMultipleResponse
                {
                    Results = new ParameterCollection
                    {
                        ["Responses"] = new ExecuteMultipleResponseItemCollection()
                    }
                });
            });

        var sut = new DeferredLookupBackfill(
            mock.Object,
            new MessageAvailabilityChecker(mock.Object, NullLogger<MessageAvailabilityChecker>.Instance),
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
            NullLogger<DeferredLookupBackfill>.Instance);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "parentcustomerid", ["account"]));

        var pool = new DataverseRecordPool();
        pool.Add("contact", [Guid.NewGuid()]);
        pool.Add("account", [Guid.NewGuid()]);

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 50);

        Assert.Empty(errors);
        Assert.Equal(2, callCount);
    }

    // ─── BackfillLookupsAsync – batching ─────────────────────────────────────

    [Fact]
    public async Task BackfillLookupsAsync_MultipleSourceRecords_CreatesMultipleBatches()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "parentcustomerid", ["account"]));

        var pool = new DataverseRecordPool();
        pool.Add("contact", [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);
        pool.Add("account", [Guid.NewGuid()]);

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 2);

        Assert.Empty(errors);
        // 3 records with batchSize 2 → chunks: [2, 1] → 2 batches
        Assert.Equal(2, captured.Count);
        Assert.Equal(2, captured[0].Requests.Count);
        Assert.Single(captured[1].Requests);
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

    [Fact]
    public async Task Backfill_preserves_constant_oneof_random_and_null_ownership()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "constid", ["account"]));
        graph.DeferEdge("contact", new DeferredLookup("contact", "oneofid", ["account"]));
        graph.DeferEdge("contact", new DeferredLookup("contact", "randomid", ["account"]));
        graph.DeferEdge("contact", new DeferredLookup("contact", "nullid", ["account"]));
        graph.DeferEdge("contact", new DeferredLookup("contact", "unruledid", ["account"]));

        var contactId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var pool = new DataverseRecordPool();
        pool.Add("contact", [contactId]);
        pool.Add("account", [accountId]);

        bool IsExplicit(string table, string column) =>
            string.Equals(table, "contact", StringComparison.OrdinalIgnoreCase)
            && column is "constid" or "oneofid" or "randomid" or "nullid";

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 50, isExplicitLookup: IsExplicit);

        Assert.Empty(errors);
        Assert.NotEmpty(captured);
        var update = Assert.IsType<UpdateRequest>(Assert.Single(Assert.Single(captured).Requests)).Target;
        Assert.True(update.Contains("unruledid"));
        var eref = Assert.IsType<EntityReference>(update["unruledid"]);
        Assert.Equal("account", eref.LogicalName);
        Assert.Equal(accountId, eref.Id);
        Assert.False(update.Contains("constid"));
        Assert.False(update.Contains("oneofid"));
        Assert.False(update.Contains("randomid"));
        Assert.False(update.Contains("nullid"));
    }

    [Fact]
    public async Task Backfill_sends_no_empty_updates()
    {
        var captured = new List<ExecuteMultipleRequest>();
        var serviceMock = BuildServiceMock(captured);
        var sut = BuildSut(serviceMock.Object);

        var graph = new DependencyGraph();
        graph.AddNode("contact");
        graph.AddNode("account");
        graph.DeferEdge("contact", new DeferredLookup("contact", "constid", ["account"]));
        graph.DeferEdge("contact", new DeferredLookup("contact", "oneofid", ["account"]));
        graph.DeferEdge("contact", new DeferredLookup("contact", "randomid", ["account"]));
        graph.DeferEdge("contact", new DeferredLookup("contact", "nullid", ["account"]));

        var pool = new DataverseRecordPool();
        pool.Add("contact", [Guid.NewGuid()]);
        pool.Add("account", [Guid.NewGuid()]);

        bool IsExplicit(string table, string column) =>
            string.Equals(table, "contact", StringComparison.OrdinalIgnoreCase)
            && column is "constid" or "oneofid" or "randomid" or "nullid";

        var errors = await sut.BackfillLookupsAsync(graph, pool, batchSize: 50, isExplicitLookup: IsExplicit);

        Assert.Empty(errors);
        Assert.Empty(captured);
        serviceMock.Verify(
            s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.DoesNotContain(captured.SelectMany(r => r.Requests), r => r is UpdateRequest or UpdateMultipleRequest);
    }
}

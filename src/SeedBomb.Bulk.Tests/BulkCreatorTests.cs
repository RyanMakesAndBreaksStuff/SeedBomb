using SeedBomb.Core.EdgeCases;
using SeedBomb.Core.Generators;
using SeedBomb.Core.Graph;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System.ServiceModel;

namespace SeedBomb.Bulk.Tests;

public class BulkCreatorTests
{
    // Helper: build a BulkCreator with real collaborators and a mocked service.
    private static (BulkCreator sut, Mock<IOrganizationServiceAsync2> serviceMock) BuildSut()
    {
        var serviceMock = new Mock<IOrganizationServiceAsync2>();

        // RetrieveMultipleAsync serves only the UpdateMultiple probe (DeferredLookupBackfill);
        // CreateMultiple availability is no longer probed.
        serviceMock
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var generatorFactory = new GeneratorFactory(NullLogger<GeneratorFactory>.Instance);
        var edgeCaseValidator = new EdgeCaseValidator(NullLogger<EdgeCaseValidator>.Instance);
        var messageChecker = new MessageAvailabilityChecker(
            serviceMock.Object,
            NullLogger<MessageAvailabilityChecker>.Instance);
        var throttlePolicy = new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance);
        var topologicalSort = new TopologicalSort(NullLogger<TopologicalSort>.Instance);
        var deferredBackfill = new DeferredLookupBackfill(
            serviceMock.Object,
            messageChecker,
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
            NullLogger<DeferredLookupBackfill>.Instance);

        var sut = new BulkCreator(
            serviceMock.Object,
            generatorFactory,
            edgeCaseValidator,
            messageChecker,
            throttlePolicy,
            topologicalSort,
            deferredBackfill,
            NullLogger<BulkCreator>.Instance);

        return (sut, serviceMock);
    }

    [Fact]
    public async Task CreateAsync_EmptyConfig_ReturnsEmptyResult()
    {
        var (sut, _) = BuildSut();

        var config = new GenerationConfig
        {
            EntityLogicalNames = [],
            RecordCounts = new Dictionary<string, int>()
        };

        var graph = new DependencyGraph();
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.NotNull(result);
        Assert.Empty(result.CreatedRecords);
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.TotalRecords);
    }

    [Fact]
    public async Task CreateAsync_EntityWithZeroRecordCount_SkipsEntity()
    {
        var (sut, _) = BuildSut();

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 0 }
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.NotNull(result);
        Assert.Empty(result.CreatedRecords);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CreateAsync_EntityMissingFromMetadata_SkipsWithoutError()
    {
        var (sut, _) = BuildSut();

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 5 }
        };

        // Empty metadata -- entity won't be found
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.NotNull(result);
        Assert.Empty(result.CreatedRecords);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CreateAsync_CancellationHonoured_ThrowsOperationCancelled()
    {
        var (sut, _) = BuildSut();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 10 }
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.CreateAsync(config, metadata, graph, ct: cts.Token));
    }

    [Fact]
    public async Task CreateAsync_NullConfig_ThrowsArgumentNullException()
    {
        var (sut, _) = BuildSut();

        var graph = new DependencyGraph();
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.CreateAsync(null!, metadata, graph));
    }

    [Fact]
    public async Task CreateAsync_NullMetadata_ThrowsArgumentNullException()
    {
        var (sut, _) = BuildSut();

        var config = new GenerationConfig
        {
            EntityLogicalNames = [],
            RecordCounts = new Dictionary<string, int>()
        };

        var graph = new DependencyGraph();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.CreateAsync(config, null!, graph));
    }

    [Fact]
    public async Task CreateAsync_NullGraph_ThrowsArgumentNullException()
    {
        var (sut, _) = BuildSut();

        var config = new GenerationConfig
        {
            EntityLogicalNames = [],
            RecordCounts = new Dictionary<string, int>()
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.CreateAsync(config, metadata, null!));
    }

    [Fact]
    public void Constructor_NullService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BulkCreator(
            null!,
            new GeneratorFactory(NullLogger<GeneratorFactory>.Instance),
            new EdgeCaseValidator(NullLogger<EdgeCaseValidator>.Instance),
            new MessageAvailabilityChecker(new Mock<IOrganizationServiceAsync2>().Object, NullLogger<MessageAvailabilityChecker>.Instance),
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
            new TopologicalSort(NullLogger<TopologicalSort>.Instance),
            new DeferredLookupBackfill(
                new Mock<IOrganizationServiceAsync2>().Object,
                new MessageAvailabilityChecker(new Mock<IOrganizationServiceAsync2>().Object, NullLogger<MessageAvailabilityChecker>.Instance),
                new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
                NullLogger<DeferredLookupBackfill>.Instance),
            NullLogger<BulkCreator>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var serviceMock = new Mock<IOrganizationServiceAsync2>();

        Assert.Throws<ArgumentNullException>(() => new BulkCreator(
            serviceMock.Object,
            new GeneratorFactory(NullLogger<GeneratorFactory>.Instance),
            new EdgeCaseValidator(NullLogger<EdgeCaseValidator>.Instance),
            new MessageAvailabilityChecker(serviceMock.Object, NullLogger<MessageAvailabilityChecker>.Instance),
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
            new TopologicalSort(NullLogger<TopologicalSort>.Instance),
            new DeferredLookupBackfill(
                serviceMock.Object,
                new MessageAvailabilityChecker(serviceMock.Object, NullLogger<MessageAvailabilityChecker>.Instance),
                new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
                NullLogger<DeferredLookupBackfill>.Instance),
            null!));
    }

    [Fact]
    public async Task CreateAsync_OwnerIdSystemRequired_OmitsFromPayloadAndDoesNotThrow()
    {
        var (sut, serviceMock) = BuildSut();

        var capturedEntities = new List<Entity>();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                if (req is CreateMultipleRequest)
                {
                    // Runtime rejection (as for OOB tables like account): BulkCreator must catch
                    // this, mark the entity, and fall back to ExecuteMultiple.
                    throw new FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040800) });
                }

                if (req is ExecuteMultipleRequest emr)
                {
                    var responses = new ExecuteMultipleResponseItemCollection();
                    for (int i = 0; i < emr.Requests.Count; i++)
                    {
                        if (emr.Requests[i] is CreateRequest cr)
                            capturedEntities.Add(cr.Target);
                        var createResp = new CreateResponse { Results = { ["id"] = Guid.NewGuid() } };
                        responses.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = createResp });
                    }
                    return new ExecuteMultipleResponse { Results = { ["Responses"] = responses } };
                }
                return new OrganizationResponse();
            });

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var ownerLookup = new LookupAttributeMetadata
        {
            LogicalName = "ownerid",
            Targets = ["systemuser", "team"]
        };
        ownerLookup.GetType().GetProperty("RequiredLevel")!.SetValue(
            ownerLookup,
            new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired));

        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[] { ownerLookup });

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
            BatchSize = 10
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = meta
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.NotNull(result);
        Assert.Empty(result.Errors);
        Assert.Single(capturedEntities);
        Assert.False(capturedEntities[0].Contains("ownerid"),
            "ownerid must be omitted so Dataverse defaults to the calling user");
    }

    [Fact]
    public async Task CreateAsync_NonOwnerIdSystemRequiredLookupWithoutGenerator_StillThrows()
    {
        var (sut, _) = BuildSut();

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var customLookup = new LookupAttributeMetadata
        {
            LogicalName = "new_customrequiredlookupid",
            Targets = ["contact"]
        };
        customLookup.GetType().GetProperty("RequiredLevel")!.SetValue(
            customLookup,
            new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired));

        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[] { customLookup });

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
            BatchSize = 10
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = meta
        };

        var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
            sut.CreateAsync(config, metadata, graph));
        Assert.Contains("SystemRequired", ex.Message);
        Assert.Contains("new_customrequiredlookupid", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_EmptyConfig_ReturnsNonNegativeElapsedTime()
    {
        var (sut, _) = BuildSut();

        var config = new GenerationConfig
        {
            EntityLogicalNames = [],
            RecordCounts = new Dictionary<string, int>()
        };

        var graph = new DependencyGraph();
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task CreateAsync_WholeBatchFails_ReportsEveryLostRow()
    {
        // CreateMultiple is transactional: one rejected request loses the whole batch.
        var (sut, serviceMock) = BuildSut();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProtocolException("The remote server returned an unexpected response: (429)  ."));

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 10 },
            BatchSize = 10,
            MaxRetries = 0,
            MaxParallelism = 1,
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.Equal(0, result.TotalRecords);
        var error = Assert.Single(result.Errors);
        Assert.Equal(10, error.RowCount);
    }

    [Fact]
    public async Task CreateAsync_WholeBatchFault_KeepsTheDataverseFaultCode()
    {
        // CR-001: RejectionClassifier reads FaultCode to tell service-protection throttling apart.
        var (sut, serviceMock) = BuildSut();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault
            {
                ErrorCode = -2147015902,
                Message = "Number of requests exceeded the limit of 6000 over time window of 300 seconds.",
            }));

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 10 },
            BatchSize = 10,
            MaxRetries = 0,
            MaxParallelism = 1,
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        var error = Assert.Single(result.Errors);
        Assert.Equal(-2147015902, error.FaultCode);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task CreateAsync_UnreachableEndpoint_MarksTheBatchTransient()
    {
        // A dropped network surfaces as EndpointNotFoundException; Retry must be offered for it.
        var (sut, serviceMock) = BuildSut();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EndpointNotFoundException(
                "There was no endpoint listening at https://contoso.crm.dynamics.com."));

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 10 },
            BatchSize = 10,
            MaxRetries = 0,
            MaxParallelism = 1,
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        var error = Assert.Single(result.Errors);
        Assert.True(error.IsTransient);
        Assert.Equal(10, error.RowCount);
    }

    [Fact]
    public async Task CreateAsync_CancelledMidEntity_ReturnsRowsAlreadyWritten()
    {
        // WR-002: cancel does not roll back, so the rows already written must come back with their IDs.
        var (sut, serviceMock) = BuildSut();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken token) =>
            {
                if (++calls > 1)
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                var cmr = (CreateMultipleRequest)req;
                return new CreateMultipleResponse { Results = { ["Ids"] = cmr.Targets.Entities.Select(e => Guid.NewGuid()).ToArray() } };
            });

        var graph = new DependencyGraph();
        graph.AddNode("account");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 20 },
            BatchSize = 10,
            MaxParallelism = 1,
        };

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        var result = await sut.CreateAsync(config, metadata, graph, ct: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(10, result.CreatedRecords["account"].Count);
    }

    [Fact]
    public async Task CreateAsync_BatchSizeAboveExecuteMultipleCap_ThrowsBeforeAnyRequest()
    {
        var (sut, serviceMock) = BuildSut();
        var graph = new DependencyGraph();
        graph.AddNode("account");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
            BatchSize = 1001,
        };

        await Assert.ThrowsAsync<DataGenerationException>(() =>
            sut.CreateAsync(config, new Dictionary<string, EntityMetadata>(), graph));
        serviceMock.Verify(
            s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static EntityMetadata Meta(string name, params AttributeMetadata[] attrs)
    {
        var meta = new EntityMetadata { LogicalName = name };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, attrs);
        // Real metadata always carries it; PreparedLookupRun reads in-run lookup targets through it.
        meta.GetType().GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!.SetValue(meta, name + "id");
        return meta;
    }

    private static LookupAttributeMetadata RequiredLookup(string name, string target)
    {
        var attr = new LookupAttributeMetadata { LogicalName = name, Targets = [target] };
        attr.GetType().GetProperty("RequiredLevel")!.SetValue(
            attr, new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired));
        return attr;
    }

    [Fact]
    public async Task CreateAsync_RequiredLookupGapOnSecondTable_WritesNothing()
    {
        // CR-003: the check ran inside the write loop, after the first table was committed.
        var (sut, serviceMock) = BuildSut();
        AnswerCreates(serviceMock);
        var graph = new DependencyGraph();
        graph.AddNode("account");
        graph.AddNode("contact");
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = Meta("account", new StringAttributeMetadata { LogicalName = "name", MaxLength = 50 }),
            // Points outside the run: no table in this run creates new_external, so only a rule can supply it.
            ["contact"] = Meta("contact", RequiredLookup("new_requiredid", "new_external")),
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account", "contact"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 1, ["contact"] = 1 },
            BatchSize = 10,
        };

        var ex = await Assert.ThrowsAsync<DataGenerationException>(() => sut.CreateAsync(config, metadata, graph));

        Assert.Contains("new_requiredid", ex.Message, StringComparison.Ordinal);
        serviceMock.Verify(s => s.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r is CreateMultipleRequest || r is ExecuteMultipleRequest),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_RequiredLookupToTableCreatedEarlier_NeedsNoRule()
    {
        // CR-003 amendment: the run creates account before contact, so the lookup is filled from the in-run pool.
        var (sut, serviceMock) = BuildSut();
        var captured = AnswerCreates(serviceMock);
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        var lookup = RequiredLookup("new_requiredid", "account");
        lookup.IsValidForCreate = true; // FieldFilter only generates creatable columns
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = Meta("account", new StringAttributeMetadata { LogicalName = "name", MaxLength = 50 }),
            ["contact"] = Meta("contact", lookup),
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account", "contact"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 1, ["contact"] = 1 },
            BatchSize = 10,
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.Single(result.CreatedRecords["account"]);
        Assert.Single(result.CreatedRecords["contact"]);
        var contact = Assert.Single(captured, e => e.LogicalName == "contact");
        var reference = Assert.IsType<EntityReference>(contact["new_requiredid"]);
        Assert.Equal("account", reference.LogicalName);
        Assert.Contains(reference.Id, result.CreatedRecords["account"]);
    }

    [Fact]
    public async Task CreateAsync_RequiredLookupToTableCreatedEarly_KeepsAnExplicitRule()
    {
        // The implicit lookupRandom rule must never replace a user's rule on the same column.
        var external = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var (sut, serviceMock) = BuildSut();
        var captured = AnswerCreates(serviceMock);
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        var lookup = RequiredLookup("new_requiredid", "account");
        lookup.IsValidForCreate = true;
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = Meta("account", new StringAttributeMetadata { LogicalName = "name", MaxLength = 50 }),
            ["contact"] = Meta("contact", lookup),
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account", "contact"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 1, ["contact"] = 1 },
            BatchSize = 10,
            FieldRules = new Dictionary<string, Dictionary<string, SeedBomb.Core.Rules.FieldRule>>
            {
                ["contact"] = new()
                {
                    ["new_requiredid"] = new SeedBomb.Core.Rules.ConstantRule(System.Text.Json.JsonDocument.Parse(
                        $$"""{"entity":"account","id":"{{external}}"}""").RootElement),
                },
            },
        };

        await sut.CreateAsync(config, metadata, graph);

        var contact = Assert.Single(captured, e => e.LogicalName == "contact");
        Assert.Equal(external, Assert.IsType<EntityReference>(contact["new_requiredid"]).Id);
    }

    [Fact]
    public async Task CreateAsync_FailureAfterAWrite_ReturnsWrittenIds()
    {
        // CR-003: any non-cancel exception after a table committed discarded allCreatedRecords.
        // The opening "Linking" snapshot (BulkCreator.cs:156) is reported after the last table commits.
        var (sut, serviceMock) = BuildSut();
        AnswerCreates(serviceMock);
        var graph = new DependencyGraph();
        graph.AddNode("account");
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = Meta("account", new StringAttributeMetadata { LogicalName = "name", MaxLength = 50 }),
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 2 },
            BatchSize = 10,
        };

        var result = await sut.CreateAsync(config, metadata, graph, new InlineProgress(p =>
        {
            if (p.Phase == "Linking") throw new InvalidOperationException("link phase exploded");
        }));

        Assert.Equal(2, result.TotalRecords);
        Assert.False(result.Cancelled);
        Assert.Contains("link phase exploded", result.FatalError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_FailureAfterBatchWrite_ReturnsCurrentTableIds()
    {
        // CR-003: a mid-table failure must drain successful batch IDs before leaving the helper.
        var (sut, serviceMock) = BuildSut();
        AnswerCreates(serviceMock);
        var graph = new DependencyGraph();
        graph.AddNode("account");
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = Meta("account", new StringAttributeMetadata { LogicalName = "name", MaxLength = 50 }),
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 4 },
            BatchSize = 2,
            MaxParallelism = 1,
        };

        var result = await sut.CreateAsync(config, metadata, graph, new InlineProgress(p =>
        {
            if (p.Phase == "Generating") throw new InvalidOperationException("create progress exploded");
        }));

        Assert.Equal(2, result.TotalRecords);
        Assert.Equal(2, result.CreatedRecords["account"].Count);
        Assert.False(result.Cancelled);
        Assert.Equal("create progress exploded", result.FatalError);
    }

    // Progress<T> posts to the thread pool; this reports inline so a throw lands inside CreateAsync.
    private sealed class InlineProgress(Action<SeedBomb.Bulk.Contracts.BulkCreationProgress> report)
        : IProgress<SeedBomb.Bulk.Contracts.BulkCreationProgress>
    {
        public void Report(SeedBomb.Bulk.Contracts.BulkCreationProgress value) => report(value);
    }

    // CreateMultiple → "unsupported" fault, so every create goes through ExecuteMultiple. Each
    // CreateRequest gets a new id unless reject(target) is true, which returns a throttle fault.
    private static List<Entity> AnswerCreates(
        Mock<IOrganizationServiceAsync2> serviceMock, Func<Entity, bool>? reject = null)
    {
        var captured = new List<Entity>();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                if (req is CreateMultipleRequest)
                    throw new FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040800) });
                if (req is not ExecuteMultipleRequest emr)
                    return new OrganizationResponse();

                var responses = new ExecuteMultipleResponseItemCollection();
                for (int i = 0; i < emr.Requests.Count; i++)
                {
                    if (emr.Requests[i] is not CreateRequest cr)
                    {
                        responses.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = new OrganizationResponse() });
                        continue;
                    }

                    lock (captured) captured.Add(cr.Target);
                    responses.Add(reject?.Invoke(cr.Target) == true
                        ? new ExecuteMultipleResponseItem
                        {
                            RequestIndex = i,
                            Fault = new OrganizationServiceFault { ErrorCode = -2147015902, Message = "request throttled" },
                        }
                        : new ExecuteMultipleResponseItem
                        {
                            RequestIndex = i,
                            Response = new CreateResponse { Results = { ["id"] = Guid.NewGuid() } },
                        });
                }

                return new ExecuteMultipleResponse { Results = { ["Responses"] = responses } };
            });
        return captured;
    }
}

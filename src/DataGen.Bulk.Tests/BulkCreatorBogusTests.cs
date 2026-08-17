using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DataGen.Bulk.Tests;

public class BulkCreatorBogusTests
{
    [Fact]
    public async Task MultiTableLateFailure_CausesZeroDataverseMutations()
    {
        var retrieve = new List<QueryBase>();
        var execute = new List<OrganizationRequest>();
        var (sut, metadata, graph) = Harness(["account", "contact"], oversizedOn: "contact", retrieve, execute);

        var ex = await Assert.ThrowsAsync<DataGenerationException>(
            () => sut.CreateAsync(Config(["account", "contact"], oversizedOn: "contact"), metadata, graph));

        Assert.Empty(retrieve);
        Assert.Empty(execute);
        Assert.Contains("Length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreparedCacheValue_EqualsWrittenCreateTarget()
    {
        var retrieve = new List<QueryBase>();
        var execute = new List<OrganizationRequest>();
        var captured = new List<Entity>();
        var (sut, metadata, graph) = Harness(["account"], oversizedOn: null, retrieve, execute, captured: captured);
        var config = Config(["account"], oversizedOn: null, seed: 42, count: 10);

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.Equal(10, result.TotalRecords);
        Assert.Equal(10, captured.Count);
        Assert.Equal("Kurtis", captured[0]["name"]);

        var attr = metadata["account"].Attributes!.Single(a => a.LogicalName == "name");
        var ctx = new RuleEvaluationContext("account", 42, "en", "t9", 10);
        var prepared = BogusRulePreparer.CompileRule(new BogusRule("NAME", "firstName", 1), attr, ctx);
        using var session = new BogusEvaluatorSession("en");
        Assert.Equal(session.Evaluate(prepared, attr, ctx, 0), captured[0]["name"]);
        Assert.Equal(session.Evaluate(prepared, attr, ctx, 9), captured[9]["name"]);
    }

    [Fact]
    public async Task CreateMultipleAndExecuteMultiple_WriteTheSameBogusValues()
    {
        var fallback = new List<Entity>();
        var multiple = new List<Entity>();
        var (sutA, metaA, graphA) = Harness(["account"], null, [], [], captured: fallback);
        var (sutB, metaB, graphB) = Harness(["account"], null, [], [], captured: multiple, createMultiple: true);
        var config = Config(["account"], null, seed: 42, count: 8);

        await sutA.CreateAsync(config, metaA, graphA);
        await sutB.CreateAsync(config, metaB, graphB);

        Assert.Equal(8, fallback.Count);
        Assert.Equal(8, multiple.Count);
        for (var i = 0; i < 8; i++)
            Assert.Equal(fallback[i]["name"], multiple[i]["name"]);
    }

    private static (BulkCreator sut, Dictionary<string, EntityMetadata> metadata, DependencyGraph graph) Harness(
        string[] tables,
        string? oversizedOn,
        List<QueryBase> retrieve,
        List<OrganizationRequest> execute,
        bool createMultiple = false,
        List<Entity>? captured = null)
    {
        var serviceMock = new Mock<IOrganizationServiceAsync2>();
        serviceMock
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => retrieve.Add(q))
            .ReturnsAsync(createMultiple
                ? new EntityCollection([new Entity("sdkmessagefilter")])
                : new EntityCollection());

        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((r, _) =>
            {
                execute.Add(r);
                Capture(r, captured);
            })
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) => Respond(req));

        var generatorFactory = new GeneratorFactory(NullLogger<GeneratorFactory>.Instance);
        var edgeCaseValidator = new EdgeCaseValidator(NullLogger<EdgeCaseValidator>.Instance);
        var messageChecker = new MessageAvailabilityChecker(
            serviceMock.Object, NullLogger<MessageAvailabilityChecker>.Instance);
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

        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);
        var graph = new DependencyGraph();
        foreach (var table in tables)
        {
            graph.AddNode(table);
            metadata[table] = TableMeta(table, oversizedOn == table ? 12 : 100);
        }

        return (sut, metadata, graph);
    }

    private static GenerationConfig Config(string[] tables, string? oversizedOn, int seed = 42, int count = 10) => new()
    {
        EntityLogicalNames = tables,
        RecordCounts = tables.ToDictionary(t => t, _ => count, StringComparer.OrdinalIgnoreCase),
        BatchSize = 50,
        Seed = seed,
        Locale = "en",
        RunId = "t9",
        FieldRules = tables.ToDictionary(
            t => t,
            t => new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = oversizedOn == t
                    ? new BogusRule("COMPANY", "catchPhrase", 1)
                    : new BogusRule("NAME", "firstName", 1),
            },
            StringComparer.OrdinalIgnoreCase),
    };

    private static EntityMetadata TableMeta(string logicalName, int maxLength)
    {
        var meta = new EntityMetadata { LogicalName = logicalName };
        var name = new StringAttributeMetadata { LogicalName = "name", MaxLength = maxLength, IsValidForCreate = true };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[] { name });
        return meta;
    }

    private static OrganizationResponse Respond(OrganizationRequest req)
    {
        if (req is ExecuteMultipleRequest emr)
        {
            var responses = new ExecuteMultipleResponseItemCollection();
            for (var i = 0; i < emr.Requests.Count; i++)
            {
                var createResp = new CreateResponse { Results = { ["id"] = Guid.NewGuid() } };
                responses.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = createResp });
            }

            return new ExecuteMultipleResponse { Results = { ["Responses"] = responses } };
        }

        if (req is CreateMultipleRequest cmr)
        {
            var ids = cmr.Targets.Entities.Select(_ => Guid.NewGuid()).ToArray();
            return new CreateMultipleResponse { Results = { ["Ids"] = ids } };
        }

        return new OrganizationResponse();
    }

    private static void Capture(OrganizationRequest req, List<Entity>? captured)
    {
        if (captured is null)
            return;
        if (req is ExecuteMultipleRequest emr)
        {
            foreach (var inner in emr.Requests.OfType<CreateRequest>())
                captured.Add(inner.Target);
        }
        else if (req is CreateMultipleRequest cmr)
        {
            captured.AddRange(cmr.Targets.Entities);
        }
    }
}

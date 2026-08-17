using System.Reflection;
using DataGen.Core.Rules;

namespace DataGen.Integration.Tests;

/// <summary>
/// Integration tests that verify the Core + Bulk pipeline end-to-end using a mocked
/// IOrganizationServiceAsync2.  XrmMockup365 is available for full in-memory Dataverse
/// simulation once entity metadata XML files are generated from a real environment.
/// </summary>
public class BulkCreatorPipelineTests
{
    // --- Helpers -------------------------------------------------------

    private static void SetReadOnly(object obj, string propertyName, object value)
    {
        var type = obj.GetType();
        while (type is not null)
        {
            var prop = type.GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (prop?.CanWrite == true) { prop.SetValue(obj, value); return; }
            var field = type.GetField($"<{propertyName}>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field is not null) { field.SetValue(obj, value); return; }
            type = type.BaseType;
        }
    }

    private static EntityMetadata MakeEntity(string logicalName, params AttributeMetadata[] attrs)
    {
        var e = new EntityMetadata { LogicalName = logicalName };
        if (attrs.Length > 0)
            SetReadOnly(e, "Attributes", attrs);
        return e;
    }

    private static Mock<IOrganizationServiceAsync2> MakeServiceMock(
        bool supportsCreateMultiple = false,
        int batchSize = 5)
    {
        var mock = new Mock<IOrganizationServiceAsync2>();

        // sdkmessagefilter query → indicates CreateMultiple support
        var sdkFilterResult = new EntityCollection(
            supportsCreateMultiple ? [new Entity("sdkmessagefilter")] : []);

        mock.Setup(s => s.RetrieveMultipleAsync(
                It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(sdkFilterResult);

        // ExecuteAsync for ExecuteMultiple
        mock.Setup(s => s.ExecuteAsync(
                It.Is<ExecuteMultipleRequest>(_ => true),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                var emr = (ExecuteMultipleRequest)req;
                var responses = new ExecuteMultipleResponseItemCollection();
                for (int i = 0; i < emr.Requests.Count; i++)
                {
                    // CreateResponse.id is a get-only computed property backed by Results["id"]
                    var createResp = new CreateResponse();
                    createResp.Results["id"] = Guid.NewGuid();
                    responses.Add(new ExecuteMultipleResponseItem
                    {
                        RequestIndex = i,
                        Response = createResp
                    });
                }
                var execMultiResp = new ExecuteMultipleResponse();
                execMultiResp.Results["Responses"] = responses;
                execMultiResp.Results["IsFaulted"] = false;
                return execMultiResp;
            });

        return mock;
    }

    private static BulkCreator MakeBulkCreator(Mock<IOrganizationServiceAsync2> serviceMock)
    {
        var service = serviceMock.Object;
        var checker = new MessageAvailabilityChecker(service, NullLogger<MessageAvailabilityChecker>.Instance);
        return new BulkCreator(
            service,
            new GeneratorFactory(NullLogger<GeneratorFactory>.Instance),
            new EdgeCaseValidator(NullLogger<EdgeCaseValidator>.Instance),
            checker,
            new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance),
            new TopologicalSort(NullLogger<TopologicalSort>.Instance),
            new DeferredLookupBackfill(service, checker, new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance), NullLogger<DeferredLookupBackfill>.Instance),
            NullLogger<BulkCreator>.Instance);
    }

    // --- Tests ---------------------------------------------------------

    [Fact]
    public async Task CreateAsync_SingleEntity_CreatesCorrectBatchCount()
    {
        var serviceMock = MakeServiceMock(supportsCreateMultiple: false);
        var creator = MakeBulkCreator(serviceMock);

        var meta = MakeEntity("new_widget",
            new StringAttributeMetadata { LogicalName = "new_name", IsValidForCreate = true, MaxLength = 100 });

        var graph = new DependencyGraph();
        graph.AddNode("new_widget");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["new_widget"],
            RecordCounts = new Dictionary<string, int> { ["new_widget"] = 10 },
            BatchSize = 5,
            Seed = 42
        };

        var entityMetadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["new_widget"] = meta
        };

        var result = await creator.CreateAsync(config, entityMetadata, graph, null, CancellationToken.None);

        // 10 records / batch of 5 = 2 ExecuteMultiple calls
        serviceMock.Verify(s => s.ExecuteAsync(
            It.Is<ExecuteMultipleRequest>(_ => true),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateAsync_TopologicalOrder_CreatesRootEntityFirst()
    {
        // contact depends on account (lookup contact→account)
        // account should be created first
        var callOrder = new List<string>();
        var serviceMock = new Mock<IOrganizationServiceAsync2>();

        // sdkmessagefilter → no CreateMultiple
        serviceMock.Setup(s => s.RetrieveMultipleAsync(
                It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection([]));

        // Track which entity is being created by inspecting the request
        serviceMock.Setup(s => s.ExecuteAsync(
                It.Is<ExecuteMultipleRequest>(_ => true),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                var emr = (ExecuteMultipleRequest)req;
                if (emr.Requests.Count > 0 && emr.Requests[0] is CreateRequest cr)
                    callOrder.Add(cr.Target.LogicalName);

                var responses = new ExecuteMultipleResponseItemCollection();
                for (int i = 0; i < emr.Requests.Count; i++)
                {
                    var createResp = new CreateResponse();
                    createResp.Results["id"] = Guid.NewGuid();
                    responses.Add(new ExecuteMultipleResponseItem
                    {
                        RequestIndex = i,
                        Response = createResp
                    });
                }
                var execMultiResp = new ExecuteMultipleResponse();
                execMultiResp.Results["Responses"] = responses;
                execMultiResp.Results["IsFaulted"] = false;
                return execMultiResp;
            });

        var creator = MakeBulkCreator(serviceMock);

        var accountMeta = MakeEntity("account",
            new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 100 });

        var contactMeta = MakeEntity("contact",
            new StringAttributeMetadata { LogicalName = "firstname", IsValidForCreate = true, MaxLength = 50 },
            new LookupAttributeMetadata
            {
                LogicalName = "accountid",
                IsValidForCreate = true,
                Targets = ["account"],
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
            });

        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account"); // contact depends on account

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["contact", "account"],
            RecordCounts = new Dictionary<string, int> { ["contact"] = 2, ["account"] = 2 },
            BatchSize = 10,
            Seed = 42
        };

        var entityMetadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = contactMeta,
            ["account"] = accountMeta
        };

        await creator.CreateAsync(config, entityMetadata, graph, null, CancellationToken.None);

        // Account must have been batched before contact
        Assert.True(callOrder.Count >= 2, "Expected at least 2 batch calls");
        Assert.Equal("account", callOrder[0]);
        Assert.Equal("contact", callOrder[1]);
    }

    /// <summary>Synchronous IProgress implementation to avoid SynchronizationContext timing issues in tests.</summary>
    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    [Fact]
    public async Task CreateAsync_ProgressReported_ForEachBatch()
    {
        var serviceMock = MakeServiceMock(supportsCreateMultiple: false);
        var creator = MakeBulkCreator(serviceMock);

        var meta = MakeEntity("new_widget",
            new StringAttributeMetadata { LogicalName = "new_name", IsValidForCreate = true, MaxLength = 100 });

        var graph = new DependencyGraph();
        graph.AddNode("new_widget");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["new_widget"],
            RecordCounts = new Dictionary<string, int> { ["new_widget"] = 6 },
            BatchSize = 2,
            Seed = 42
        };

        var progressReports = new List<BulkCreationProgress>();
        var progress = new SyncProgress<BulkCreationProgress>(p => progressReports.Add(p));

        await creator.CreateAsync(config,
            new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["new_widget"] = meta },
            graph, progress, CancellationToken.None);

        // 6 records / batch of 2 = 3 batches → 3 progress reports
        Assert.True(progressReports.Count >= 3, $"Expected at least 3 progress reports for 3 batches, got {progressReports.Count}");
        Assert.All(progressReports, p => Assert.Equal("new_widget", p.EntityLogicalName));
    }

    [Fact]
    public async Task CreateAsync_NoRecordCount_SkipsEntity()
    {
        var serviceMock = MakeServiceMock(supportsCreateMultiple: false);
        var creator = MakeBulkCreator(serviceMock);

        var meta = MakeEntity("new_widget");
        var graph = new DependencyGraph();
        graph.AddNode("new_widget");

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["new_widget"],
            RecordCounts = new Dictionary<string, int>(), // no count for new_widget
            BatchSize = 10,
            Seed = 42
        };

        var result = await creator.CreateAsync(config,
            new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["new_widget"] = meta },
            graph, null, CancellationToken.None);

        Assert.Equal(0, result.TotalRecords);
        serviceMock.Verify(s => s.ExecuteAsync(
            It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BogusPreviewContext_MatchesWrittenProductionValues()
    {
        var captured = new List<Entity>();
        var serviceMock = MakeServiceMock(supportsCreateMultiple: false);
        serviceMock
            .Setup(s => s.ExecuteAsync(It.Is<ExecuteMultipleRequest>(_ => true), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                var emr = (ExecuteMultipleRequest)req;
                var responses = new ExecuteMultipleResponseItemCollection();
                for (var i = 0; i < emr.Requests.Count; i++)
                {
                    if (emr.Requests[i] is CreateRequest cr)
                        captured.Add(cr.Target);
                    var createResp = new CreateResponse();
                    createResp.Results["id"] = Guid.NewGuid();
                    responses.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = createResp });
                }
                var execMultiResp = new ExecuteMultipleResponse();
                execMultiResp.Results["Responses"] = responses;
                execMultiResp.Results["IsFaulted"] = false;
                return execMultiResp;
            });

        var creator = MakeBulkCreator(serviceMock);
        var name = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 100 };
        var meta = MakeEntity("account", name);
        var graph = new DependencyGraph();
        graph.AddNode("account");
        var ctx = new RuleEvaluationContext("account", Seed: 42, Locale: "en", RunId: "parity", RecordCount: 5);
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 5 },
            BatchSize = 10,
            Seed = ctx.Seed,
            Locale = ctx.Locale,
            RunId = ctx.RunId,
            FieldRules = new Dictionary<string, Dictionary<string, FieldRule>>
            {
                ["account"] = new() { ["name"] = new BogusRule("NAME", "firstName", 1) },
            },
        };

        await creator.CreateAsync(
            config,
            new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["account"] = meta },
            graph);

        Assert.Equal(5, captured.Count);
        var prepared = BogusRulePreparer.CompileRule(new BogusRule("NAME", "firstName", 1), name, ctx);
        using var session = new BogusEvaluatorSession(ctx.Locale);
        for (var row = 0; row < 5; row++)
            Assert.Equal(session.Evaluate(prepared, name, ctx, row), captured[row]["name"]);
    }
}

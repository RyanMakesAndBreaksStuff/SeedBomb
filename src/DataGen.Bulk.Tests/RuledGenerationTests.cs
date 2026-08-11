using System.Text.Json;
using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DataGen.Bulk.Tests;

public class RuledGenerationTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    // Mirrors BulkCreatorTests.BuildSut — real collaborators, mocked service, ExecuteMultiple-only
    // fallback so every CreateRequest.Target is captured for payload inspection.
    private static (BulkCreator sut, List<Entity> captured) BuildSut()
    {
        var serviceMock = new Mock<IOrganizationServiceAsync2>();
        serviceMock
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var captured = new List<Entity>();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                if (req is ExecuteMultipleRequest emr)
                {
                    var responses = new ExecuteMultipleResponseItemCollection();
                    for (int i = 0; i < emr.Requests.Count; i++)
                    {
                        if (emr.Requests[i] is CreateRequest cr)
                            captured.Add(cr.Target);
                        var createResp = new CreateResponse { Results = { ["id"] = Guid.NewGuid() } };
                        responses.Add(new ExecuteMultipleResponseItem { RequestIndex = i, Response = createResp });
                    }
                    return new ExecuteMultipleResponse { Results = { ["Responses"] = responses } };
                }
                return new OrganizationResponse();
            });

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

        return (sut, captured);
    }

    private static EntityMetadata BuildAccountMetadata()
    {
        var meta = new EntityMetadata { LogicalName = "account" };
        var name = new StringAttributeMetadata { LogicalName = "name", MaxLength = 100 };
        var employees = new IntegerAttributeMetadata { LogicalName = "numberofemployees", MinValue = 0, MaxValue = 1_000_000 };
        // "description" is last: it's the ruled column in the constant-rule test below. A ruled
        // column's legacy generator call is skipped entirely (never touches the Faker stream), so
        // placing it last keeps every PRECEDING column's Faker draws identical to the unruled run —
        // this is the byte-identity property this test actually proves.
        var description = new StringAttributeMetadata { LogicalName = "description", MaxLength = 200 };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[] { name, employees, description });
        return meta;
    }

    private static Dictionary<string, string?> Snapshot(Entity e) =>
        e.Attributes.ToDictionary(a => a.Key, a => a.Value?.ToString());

    [Fact]
    public async Task Empty_rule_set_is_byte_identical_to_no_rule_set()
    {
        var graphA = new DependencyGraph();
        graphA.AddNode("account");
        var (sutA, capturedA) = BuildSut();
        var configA = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 5 },
            BatchSize = 10,
            FieldRules = null,
        };
        await sutA.CreateAsync(configA, new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["account"] = BuildAccountMetadata() }, graphA);

        var graphB = new DependencyGraph();
        graphB.AddNode("account");
        var (sutB, capturedB) = BuildSut();
        var configB = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 5 },
            BatchSize = 10,
            FieldRules = new Dictionary<string, Dictionary<string, FieldRule>> { ["account"] = new() },
        };
        await sutB.CreateAsync(configB, new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["account"] = BuildAccountMetadata() }, graphB);

        Assert.Equal(5, capturedA.Count);
        Assert.Equal(capturedA.Count, capturedB.Count);
        for (int i = 0; i < capturedA.Count; i++)
            Assert.Equal(Snapshot(capturedA[i]), Snapshot(capturedB[i]));
    }

    [Fact]
    public async Task Constant_rule_sets_value_and_leaves_other_columns_legacy_identical()
    {
        var graphA = new DependencyGraph();
        graphA.AddNode("account");
        var (sutA, capturedA) = BuildSut();
        var configA = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 5 },
            BatchSize = 10,
        };
        await sutA.CreateAsync(configA, new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["account"] = BuildAccountMetadata() }, graphA);

        var graphB = new DependencyGraph();
        graphB.AddNode("account");
        var (sutB, capturedB) = BuildSut();
        var configB = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 5 },
            BatchSize = 10,
            FieldRules = new Dictionary<string, Dictionary<string, FieldRule>>
            {
                ["account"] = new() { ["description"] = new ConstantRule(J("\"[MARKER]\"")) },
            },
        };
        await sutB.CreateAsync(configB, new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["account"] = BuildAccountMetadata() }, graphB);

        Assert.Equal(5, capturedB.Count);
        for (int i = 0; i < capturedB.Count; i++)
        {
            Assert.Equal("[MARKER]", capturedB[i]["description"]);

            var snapA = Snapshot(capturedA[i]);
            var snapB = Snapshot(capturedB[i]);
            foreach (var key in snapA.Keys.Where(k => k != "description"))
                Assert.Equal(snapA[key], snapB[key]);
        }
    }
    // Fabricates a single-attribute EntityMetadata routed through the special-handling loop
    // (Money/DateTime/RichText all route via GetRoutableSpecialHandlingAttributes, never
    // GetGeneratableAttributes) so a rule on it is applied in that loop, not the normal one.
    private static EntityMetadata BuildSpecialHandlingMetadata(AttributeMetadata attr)
    {
        var meta = new EntityMetadata { LogicalName = "ruled_special" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new[] { attr });
        return meta;
    }

    [Theory]
    [InlineData("revenue")]      // Money: special-handling loop
    [InlineData("sampledate")]   // DateTime: special-handling loop
    [InlineData("richtext")]     // rich-text Memo: special-handling loop
    public async Task Rule_replaces_supported_special_handling_value(string logicalName)
    {
        AttributeMetadata attr;
        FieldRule rule;
        object expected;

        switch (logicalName)
        {
            case "revenue":
                attr = new MoneyAttributeMetadata { LogicalName = logicalName, MinValue = 0, MaxValue = 100_000, Precision = 2 };
                rule = new ConstantRule(J("12345.67"));
                expected = new Money(12345.67m);
                break;
            case "sampledate":
                attr = new DateTimeAttributeMetadata { LogicalName = logicalName };
                rule = new ConstantRule(J("\"2031-06-15T00:00:00\""));
                expected = DateTime.SpecifyKind(DateTime.Parse("2031-06-15T00:00:00"), DateTimeKind.Utc);
                break;
            case "richtext":
                attr = new MemoAttributeMetadata { LogicalName = logicalName, MaxLength = 500 };
                ((MemoAttributeMetadata)attr).FormatName = new MemoFormatName { Value = "RichText" };
                rule = new ConstantRule(J("\"<p>Ruled content</p>\""));
                expected = "<p>Ruled content</p>";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(logicalName));
        }

        var graph = new DependencyGraph();
        graph.AddNode("ruled_special");
        var (sut, captured) = BuildSut();
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["ruled_special"],
            RecordCounts = new Dictionary<string, int> { ["ruled_special"] = 2 },
            BatchSize = 10,
            FieldRules = new Dictionary<string, Dictionary<string, FieldRule>>
            {
                ["ruled_special"] = new() { [logicalName] = rule },
            },
        };

        await sut.CreateAsync(config, new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["ruled_special"] = BuildSpecialHandlingMetadata(attr) }, graph);

        Assert.Equal(2, captured.Count);
        foreach (var entity in captured)
        {
            if (expected is Money expectedMoney)
                Assert.Equal(expectedMoney.Value, ((Money)entity[logicalName]).Value);
            else
                Assert.Equal(expected, entity[logicalName]);
        }
    }

    private static EntityMetadata BuildMetaWithAttribute(AttributeMetadata attr, EntityKeyMetadata[]? keys = null)
    {
        var meta = new EntityMetadata { LogicalName = "ruled_ineligible" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new[] { attr });
        if (keys is not null)
            meta.GetType().GetProperty("Keys")!.SetValue(meta, keys);
        return meta;
    }

    [Fact]
    public async Task Status_alternate_key_lookup_and_multiselect_rules_fail_before_any_create_call()
    {
        // statecode is platform-owned state — RuleEligibility.StateCode.
        var statecodeAttr = new StateAttributeMetadata { LogicalName = "statecode" };
        var statecodeMeta = BuildMetaWithAttribute(statecodeAttr);

        // externalid is part of an alternate key — rejected by BulkCreator's own preflight check
        // (RuleEligibility can't see entity.Keys, so this is not RuleValidator's job).
        var altKeyAttr = new StringAttributeMetadata { LogicalName = "externalid", MaxLength = 50 };
        var altKey = new EntityKeyMetadata { LogicalName = "externalid_key", KeyAttributes = ["externalid"] };
        var altKeyMeta = BuildMetaWithAttribute(altKeyAttr, [altKey]);

        // Lookups are out of scope in v1 — RuleEligibility.Lookup.
        var lookupAttr = new LookupAttributeMetadata { LogicalName = "parentcustomerid", Targets = ["account"] };
        var lookupMeta = BuildMetaWithAttribute(lookupAttr);

        // MultiSelect editing is v2 — RuleEligibility.MultiSelectV2.
        var multiSelectAttr = new MultiSelectPicklistAttributeMetadata { LogicalName = "multipick" };
        var multiSelectMeta = BuildMetaWithAttribute(multiSelectAttr);

        var cases = new (string EntityName, EntityMetadata Meta, string LogicalName)[]
        {
            ("ruled_ineligible", statecodeMeta, "statecode"),
            ("ruled_ineligible", altKeyMeta, "externalid"),
            ("ruled_ineligible", lookupMeta, "parentcustomerid"),
            ("ruled_ineligible", multiSelectMeta, "multipick"),
        };

        foreach (var (entityName, meta, logicalName) in cases)
        {
            var graph = new DependencyGraph();
            graph.AddNode(entityName);
            var (sut, captured) = BuildSut();
            var config = new GenerationConfig
            {
                EntityLogicalNames = [entityName],
                RecordCounts = new Dictionary<string, int> { [entityName] = 3 },
                BatchSize = 10,
                FieldRules = new Dictionary<string, Dictionary<string, FieldRule>>
                {
                    [entityName] = new() { [logicalName] = new ConstantRule(J("1")) },
                },
            };

            var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
                sut.CreateAsync(config, new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { [entityName] = meta }, graph));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.Contains(logicalName, ex.Message);
            Assert.Empty(captured);
        }
    }
}

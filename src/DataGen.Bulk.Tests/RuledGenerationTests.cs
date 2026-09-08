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
    private static (BulkCreator sut, List<Entity> captured) BuildSut(
        IReadOnlyList<Entity>? currencyEntities = null)
    {
        var serviceMock = new Mock<IOrganizationServiceAsync2>();
        serviceMock
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase q, CancellationToken _) =>
            {
                if (currencyEntities is not null
                    && q is QueryExpression qe
                    && string.Equals(qe.EntityName, "transactioncurrency", StringComparison.OrdinalIgnoreCase))
                    return new EntityCollection([.. currencyEntities]);
                return new EntityCollection();
            });

        var captured = new List<Entity>();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                if (req is CreateMultipleRequest cmr)
                {
                    foreach (var entity in cmr.Targets.Entities)
                        captured.Add(entity);
                    return new OrganizationResponse();
                }
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
    public async Task System_required_lookup_accepts_valid_explicit_constant()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid", Targets = ["account"], IsValidForCreate = true,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
        };
        var graph = new DependencyGraph();
        graph.AddNode("ruled_ineligible");
        var (sut, captured) = BuildSut();
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["ruled_ineligible"],
            RecordCounts = new() { ["ruled_ineligible"] = 2 },
            FieldRules = new()
            {
                ["ruled_ineligible"] = new()
                {
                    ["parentaccountid"] = new ConstantRule(J(
                        """{"entity":"account","id":"11111111-1111-1111-1111-111111111111"}""")),
                },
            },
        };
        await sut.CreateAsync(config,
            new Dictionary<string, EntityMetadata> { ["ruled_ineligible"] = BuildMetaWithAttribute(attr) }, graph);
        Assert.Equal(2, captured.Count);
        Assert.All(captured, e => Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Assert.IsType<EntityReference>(e["parentaccountid"]).Id));
    }

    [Fact]
    public async Task Status_alternate_key_malformed_lookup_and_multiselect_rules_fail_before_any_create_call()
    {
        // statecode is platform-owned state — RuleEligibility.StateCode.
        var statecodeAttr = new StateAttributeMetadata { LogicalName = "statecode" };
        var statecodeMeta = BuildMetaWithAttribute(statecodeAttr);

        // externalid is part of an alternate key — rejected by BulkCreator's own preflight check
        // (RuleEligibility can't see entity.Keys, so this is not RuleValidator's job).
        var altKeyAttr = new StringAttributeMetadata { LogicalName = "externalid", MaxLength = 50 };
        var altKey = new EntityKeyMetadata { LogicalName = "externalid_key", KeyAttributes = ["externalid"] };
        var altKeyMeta = BuildMetaWithAttribute(altKeyAttr, [altKey]);

        // Numeric lookup values are malformed lookup data, not a blanket lookup ban.
        var lookupAttr = new LookupAttributeMetadata { LogicalName = "parentcustomerid", Targets = ["account"] };
        var lookupMeta = BuildMetaWithAttribute(lookupAttr);

        // Owner is assigned by the platform — RuleEligibility.OwnerAssigned.
        var ownerAttr = new LookupAttributeMetadata { LogicalName = "ownerid", Targets = ["systemuser", "team"] };
        var ownerMeta = BuildMetaWithAttribute(ownerAttr);

        // Non-customer multi-target lookups are unsupported — RuleEligibility.Lookup.
        var regardingAttr = new LookupAttributeMetadata { LogicalName = "regardingobjectid", Targets = ["account", "contact"] };
        var regardingMeta = BuildMetaWithAttribute(regardingAttr);

        // MultiSelect editing is v2 — RuleEligibility.MultiSelectV2.
        var multiSelectAttr = new MultiSelectPicklistAttributeMetadata { LogicalName = "multipick" };
        var multiSelectMeta = BuildMetaWithAttribute(multiSelectAttr);

        var cases = new (string EntityName, EntityMetadata Meta, string LogicalName)[]
        {
            ("ruled_ineligible", statecodeMeta, "statecode"),
            ("ruled_ineligible", altKeyMeta, "externalid"),
            ("ruled_ineligible", lookupMeta, "parentcustomerid"),
            ("ruled_ineligible", ownerMeta, "ownerid"),
            ("ruled_ineligible", regardingMeta, "regardingobjectid"),
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

    private static EntityMetadata BuildNamedMeta(string logicalName, params AttributeMetadata[] attrs)
    {
        var meta = new EntityMetadata { LogicalName = logicalName };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, attrs);
        return meta;
    }

    [Fact]
    public async Task Explicit_constant_breaks_an_otherwise_required_cycle()
    {
        var alphaLookup = new LookupAttributeMetadata
        {
            LogicalName = "betaid",
            Targets = ["beta"],
            IsValidForCreate = true,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
        };
        var betaLookup = new LookupAttributeMetadata
        {
            LogicalName = "alphaid",
            Targets = ["alpha"],
            IsValidForCreate = true,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
        };
        var meta = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = BuildNamedMeta("alpha", alphaLookup),
            ["beta"] = BuildNamedMeta("beta", betaLookup),
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["alpha", "beta"],
            RecordCounts = new() { ["alpha"] = 2, ["beta"] = 0 },
            FieldRules = new()
            {
                ["alpha"] = new()
                {
                    ["betaid"] = new ConstantRule(J(
                        """{"entity":"beta","id":"11111111-1111-1111-1111-111111111111"}""")),
                },
            },
        };

        var builder = new GraphBuilder(NullLogger<GraphBuilder>.Instance);
        var detector = new CycleDetector(NullLogger<CycleDetector>.Instance);
        var unruledGraph = builder.Build(meta);
        var unruledCycles = detector.FindStronglyConnectedComponents(unruledGraph);
        Assert.NotEmpty(unruledCycles);
        Assert.Throws<UnbreakableCycleException>(() => detector.BreakCycles(unruledGraph, unruledCycles, meta));

        bool IsExplicitLookup(string table, string column) =>
            LookupRulePolicy.IsExplicit(config.FieldRules, table, column);
        var graph = builder.Build(meta, IsExplicitLookup);
        var cycles = detector.FindStronglyConnectedComponents(graph);
        if (cycles.Count > 0)
            detector.BreakCycles(graph, cycles, meta, IsExplicitLookup);

        var (sut, captured) = BuildSut();
        await sut.CreateAsync(config, meta, graph);
        Assert.Equal(2, captured.Count);
        Assert.All(captured, e => Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Assert.IsType<EntityReference>(e["betaid"]).Id));
    }

    [Fact]
    public async Task Explicit_currency_rule_wins_after_money_generation()
    {
        var poolCurrencyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var ruledCurrencyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var altCurrencyId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var money = new MoneyAttributeMetadata { LogicalName = "revenue", MinValue = 0, MaxValue = 100_000, Precision = 2 };
        var currencyLookup = new LookupAttributeMetadata
        {
            LogicalName = "transactioncurrencyid",
            Targets = ["transactioncurrency"],
            IsValidForCreate = true,
        };

        async Task<List<Entity>> RunAsync(FieldRule rule)
        {
            var graph = new DependencyGraph();
            graph.AddNode("ruled_special");
            var (sut, captured) = BuildSut([new Entity("transactioncurrency") { Id = poolCurrencyId }]);
            var config = new GenerationConfig
            {
                EntityLogicalNames = ["ruled_special"],
                RecordCounts = new() { ["ruled_special"] = 2 },
                FieldRules = new()
                {
                    ["ruled_special"] = new() { ["transactioncurrencyid"] = rule },
                },
            };
            await sut.CreateAsync(config,
                new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ruled_special"] = BuildNamedMeta("ruled_special", money, currencyLookup),
                }, graph);
            return captured;
        }

        var constantCaptured = await RunAsync(new ConstantRule(J(
            """{"entity":"transactioncurrency","id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}""")));
        Assert.Equal(2, constantCaptured.Count);
        Assert.All(constantCaptured, e =>
        {
            var eref = Assert.IsType<EntityReference>(e["transactioncurrencyid"]);
            Assert.Equal("transactioncurrency", eref.LogicalName);
            Assert.Equal(ruledCurrencyId, eref.Id);
            Assert.NotEqual(poolCurrencyId, eref.Id);
        });

        var oneOfCaptured = await RunAsync(new OneOfRule(
        [
            J("""{"entity":"transactioncurrency","id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}"""),
            J("""{"entity":"transactioncurrency","id":"cccccccc-cccc-cccc-cccc-cccccccccccc"}"""),
        ], OneOfPick.Cycle));
        Assert.Equal(2, oneOfCaptured.Count);
        Assert.All(oneOfCaptured, e =>
        {
            var eref = Assert.IsType<EntityReference>(e["transactioncurrencyid"]);
            Assert.Equal("transactioncurrency", eref.LogicalName);
            Assert.Contains(eref.Id, (Guid[])[ruledCurrencyId, altCurrencyId]);
            Assert.NotEqual(poolCurrencyId, eref.Id);
        });

        var nullCaptured = await RunAsync(new NullRule());
        Assert.Equal(2, nullCaptured.Count);
        Assert.All(nullCaptured, e => Assert.False(e.Contains("transactioncurrencyid")));
    }

    [Fact]
    public async Task Invalid_lookup_on_later_table_prevents_every_create()
    {
        var accountName = new StringAttributeMetadata { LogicalName = "name", MaxLength = 100, IsValidForCreate = true };
        var contactLookup = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid",
            Targets = ["account"],
            IsValidForCreate = true,
        };
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        var (sut, captured) = BuildSut();
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account", "contact"],
            RecordCounts = new() { ["account"] = 3, ["contact"] = 2 },
            FieldRules = new()
            {
                ["contact"] = new() { ["parentaccountid"] = new ConstantRule(J("1")) },
            },
        };
        var meta = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = BuildNamedMeta("account", accountName),
            ["contact"] = BuildNamedMeta("contact", contactLookup),
        };

        var ex = await Assert.ThrowsAsync<DataGenerationException>(() => sut.CreateAsync(config, meta, graph));
        Assert.Contains("parentaccountid", ex.Message);
        Assert.Empty(captured);
    }

    [Fact]
    public async Task Lookup_before_unruled_text_preserves_legacy_stream_for_fixed_topology()
    {
        var lookup = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid",
            Targets = ["account"],
            IsValidForCreate = true,
        };
        var name = new StringAttributeMetadata { LogicalName = "name", MaxLength = 100, IsValidForCreate = true };
        var meta = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = BuildNamedMeta("contact", lookup, name),
        };

        var graphA = new DependencyGraph();
        graphA.AddNode("contact");
        var (sutA, capturedA) = BuildSut();
        await sutA.CreateAsync(new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 5 },
        }, meta, graphA);

        var graphB = new DependencyGraph();
        graphB.AddNode("contact");
        var (sutB, capturedB) = BuildSut();
        await sutB.CreateAsync(new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 5 },
            FieldRules = new()
            {
                ["contact"] = new()
                {
                    ["parentaccountid"] = new ConstantRule(J(
                        """{"entity":"account","id":"11111111-1111-1111-1111-111111111111"}""")),
                },
            },
        }, meta, graphB);

        Assert.Equal(5, capturedA.Count);
        Assert.Equal(capturedA.Count, capturedB.Count);
        for (int i = 0; i < capturedA.Count; i++)
        {
            Assert.Equal(capturedA[i]["name"], capturedB[i]["name"]);
            var ruled = Assert.IsType<EntityReference>(capturedB[i]["parentaccountid"]);
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), ruled.Id);
        }
    }
}

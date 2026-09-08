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
        IReadOnlyList<Entity>? currencyEntities = null,
        Func<QueryExpression, CancellationToken, EntityCollection>? onQuery = null,
        Func<OrganizationRequest, CancellationToken, OrganizationResponse?>? onExecute = null)
    {
        var serviceMock = new Mock<IOrganizationServiceAsync2>();
        serviceMock
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase q, CancellationToken ct) =>
            {
                if (q is QueryExpression qe)
                {
                    if (onQuery is not null)
                        return onQuery(qe, ct);
                    if (currencyEntities is not null
                        && string.Equals(qe.EntityName, "transactioncurrency", StringComparison.OrdinalIgnoreCase))
                        return new EntityCollection([.. currencyEntities]);
                }
                return new EntityCollection();
            });

        var captured = new List<Entity>();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken ct) =>
            {
                if (onExecute is not null)
                {
                    var custom = onExecute(req, ct);
                    if (custom is not null)
                        return custom;
                }
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

    [Fact]
    public async Task Empty_random_candidates_block_before_creating_any_rows()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid", Targets = ["account"], IsValidForCreate = true,
        };
        var meta = BuildMetaWithAttribute(attr);
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!
            .SetValue(meta, "ruled_ineligibleid");
        var target = new EntityMetadata { LogicalName = "account" };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!
            .SetValue(target, "accountid");
        var (sut, captured) = BuildSut();
        var graph = new DependencyGraph();
        graph.AddNode("ruled_ineligible");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["ruled_ineligible"],
            RecordCounts = new() { ["ruled_ineligible"] = 2 },
            FieldRules = new() { ["ruled_ineligible"] = new() { ["parentaccountid"] = new LookupRandomRule() } },
        };
        var error = await Assert.ThrowsAsync<DataGenerationException>(() => sut.CreateAsync(config,
            new Dictionary<string, EntityMetadata> { ["ruled_ineligible"] = meta, ["account"] = target }, graph));
        Assert.Contains("ruled_ineligible.parentaccountid", error.Message);
        Assert.Contains("2", error.Message);
        Assert.Empty(captured);
    }

    [Fact]
    public async Task Random_lookup_reads_once_per_distinct_target_before_first_create()
    {
        var parent = LookupAttr("parentaccountid", "account");
        var partner = LookupAttr("msa_managingpartnerid", "account");
        var source = WithPrimaryId(BuildNamedMeta("contact", parent, partner), "contactid");
        var account = WithPrimaryId(new EntityMetadata { LogicalName = "account" }, "accountid");
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var ops = new List<string>();
        var (sut, captured) = BuildSut(
            onQuery: (q, _) =>
            {
                ops.Add($"query:{q.EntityName}");
                if (string.Equals(q.EntityName, "account", StringComparison.OrdinalIgnoreCase))
                    return TargetEntities("account", id1, id2);
                return new EntityCollection();
            },
            onExecute: (req, _) =>
            {
                if (req is CreateMultipleRequest or ExecuteMultipleRequest)
                    ops.Add("write");
                return null;
            });
        var graph = new DependencyGraph();
        graph.AddNode("contact");
        await sut.CreateAsync(new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 3 },
            FieldRules = new()
            {
                ["contact"] = new()
                {
                    ["parentaccountid"] = new LookupRandomRule(),
                    ["msa_managingpartnerid"] = new LookupRandomRule(),
                },
            },
        }, new Dictionary<string, EntityMetadata> { ["contact"] = source, ["account"] = account }, graph);

        Assert.Equal(1, ops.Count(o => o == "query:account"));
        Assert.Contains("write", ops);
        Assert.True(ops.IndexOf("query:account") < ops.IndexOf("write"));
        Assert.Equal(3, captured.Count);
        Assert.All(captured, e =>
        {
            var parentRef = Assert.IsType<EntityReference>(e["parentaccountid"]);
            var partnerRef = Assert.IsType<EntityReference>(e["msa_managingpartnerid"]);
            Assert.Equal("account", parentRef.LogicalName);
            Assert.Equal("account", partnerRef.LogicalName);
            Assert.Contains(parentRef.Id, (Guid[])[id1, id2]);
            Assert.Contains(partnerRef.Id, (Guid[])[id1, id2]);
        });
    }

    [Fact]
    public async Task Random_lookup_targets_outside_selected_tables_are_supported()
    {
        var lookup = LookupAttr("new_vendorid", "new_vendor");
        var source = WithPrimaryId(BuildNamedMeta("contact", lookup), "contactid");
        var vendorMeta = WithPrimaryId(new EntityMetadata { LogicalName = "new_vendor" }, "new_vendorkey");
        var vendorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var retrieveFilters = new List<EntityFilters>();
        var vendorQueries = new List<(string[] Columns, string[] OrderAttrs, int? TopCount)>();
        var (sut, captured) = BuildSut(
            onQuery: (q, _) =>
            {
                if (!string.Equals(q.EntityName, "new_vendor", StringComparison.OrdinalIgnoreCase))
                    return new EntityCollection();
                vendorQueries.Add((
                    [.. q.ColumnSet.Columns],
                    [.. q.Orders.Select(o => o.AttributeName)],
                    q.TopCount));
                return TargetEntities("new_vendor", vendorId);
            },
            onExecute: (req, _) =>
            {
                if (req is not RetrieveEntityRequest retrieve)
                    return null;
                Assert.Equal("new_vendor", retrieve.LogicalName);
                retrieveFilters.Add(retrieve.EntityFilters);
                return new RetrieveEntityResponse { Results = { ["EntityMetadata"] = vendorMeta } };
            });
        var graph = new DependencyGraph();
        graph.AddNode("contact");
        await sut.CreateAsync(new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 2 },
            FieldRules = new() { ["contact"] = new() { ["new_vendorid"] = new LookupRandomRule() } },
        }, new Dictionary<string, EntityMetadata> { ["contact"] = source }, graph);

        Assert.Equal([EntityFilters.Entity], retrieveFilters);
        Assert.Single(vendorQueries);
        Assert.Equal(["new_vendorkey"], vendorQueries[0].Columns);
        Assert.Equal(["new_vendorkey"], vendorQueries[0].OrderAttrs);
        Assert.Null(vendorQueries[0].TopCount);
        Assert.Equal(2, captured.Count);
        Assert.All(captured, e =>
        {
            Assert.Equal("contact", e.LogicalName);
            var eref = Assert.IsType<EntityReference>(e["new_vendorid"]);
            Assert.Equal("new_vendor", eref.LogicalName);
            Assert.Equal(vendorId, eref.Id);
        });
        Assert.DoesNotContain(captured, e => e.LogicalName == "new_vendor");
    }

    [Fact]
    public async Task Random_lookup_paging_is_bounded_and_ordered()
    {
        var lookup = LookupAttr("parentaccountid", "account");
        var source = WithPrimaryId(BuildNamedMeta("contact", lookup), "contactid");
        var account = WithPrimaryId(new EntityMetadata { LogicalName = "account" }, "accountid");
        const string cookie1 = "account-page-1";
        var pages = new List<(int Number, int Count, string? Cookie, int? TopCount, string[] Columns, string[] OrderAttrs, OrderType[] OrderTypes, string[] Conditions)>();
        var page1Ids = SequentialIds(1, 500).ToArray();
        var page2Ids = SequentialIds(501, 500).ToArray();
        var (sut, captured) = BuildSut(onQuery: (q, _) =>
        {
            if (!string.Equals(q.EntityName, "account", StringComparison.OrdinalIgnoreCase))
                return new EntityCollection();
            var page = CopyPageInfo(q.PageInfo);
            pages.Add((
                page.PageNumber,
                page.Count,
                page.PagingCookie,
                q.TopCount,
                [.. q.ColumnSet.Columns],
                [.. q.Orders.Select(o => o.AttributeName)],
                [.. q.Orders.Select(o => o.OrderType)],
                [.. q.Criteria?.Conditions.Select(c => c.AttributeName) ?? []]));
            Assert.True(pages.Count <= 2, "Lookup capture must not issue a third data page.");
            if (page.PageNumber == 1)
            {
                Assert.True(string.IsNullOrEmpty(page.PagingCookie));
                return TargetEntities("account", page1Ids, more: true, cookie: cookie1);
            }
            Assert.Equal(2, page.PageNumber);
            Assert.Equal(cookie1, page.PagingCookie);
            return TargetEntities("account", page2Ids, more: true, cookie: "account-page-2");
        });
        var graph = new DependencyGraph();
        graph.AddNode("contact");
        await sut.CreateAsync(new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 2 },
            FieldRules = new() { ["contact"] = new() { ["parentaccountid"] = new LookupRandomRule() } },
        }, new Dictionary<string, EntityMetadata> { ["contact"] = source, ["account"] = account }, graph);

        Assert.Equal(2, pages.Count);
        Assert.All(pages, p =>
        {
            Assert.Equal(500, p.Count);
            Assert.Null(p.TopCount);
            Assert.Equal(["accountid"], p.Columns);
            Assert.Equal(["accountid"], p.OrderAttrs);
            Assert.Equal([OrderType.Ascending], p.OrderTypes);
            Assert.DoesNotContain("statecode", p.Conditions);
        });
        Assert.Equal(1, pages[0].Number);
        Assert.Equal(2, pages[1].Number);
        Assert.Equal(cookie1, pages[1].Cookie);
        var allowed = page1Ids.Concat(page2Ids).ToHashSet();
        Assert.Equal(2, captured.Count);
        Assert.All(captured, e =>
        {
            var eref = Assert.IsType<EntityReference>(e["parentaccountid"]);
            Assert.Equal("account", eref.LogicalName);
            Assert.Contains(eref.Id, allowed);
        });
    }

    [Fact]
    public async Task Random_lookup_uses_canonical_candidate_order()
    {
        var idA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var idB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var forward = await RunOrderedCandidatesAsync([idA, idB]);
        var reversed = await RunOrderedCandidatesAsync([idB, idA]);
        Assert.Equal(forward.Count, reversed.Count);
        for (var i = 0; i < forward.Count; i++)
        {
            Assert.Equal(forward[i].LogicalName, reversed[i].LogicalName);
            Assert.Equal(forward[i].Id, reversed[i].Id);
        }
    }

    [Fact]
    public async Task Random_lookup_changes_neither_choice_nor_candidates_with_batch_size()
    {
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var id3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var small = await RunBatchSizeAsync(1, 1, id1, id2, id3);
        var large = await RunBatchSizeAsync(7, 4, id1, id2, id3);
        Assert.Equal(small.Count, large.Count);
        foreach (var (name, reference) in small)
            Assert.Equal(reference, large[name]);
    }

    [Fact]
    public async Task Random_lookup_empty_or_faulted_later_target_makes_zero_writes()
    {
        var accountName = NameAttr();
        var contactLookup = LookupAttr("parentaccountid", "account");
        var meta = new Dictionary<string, EntityMetadata>
        {
            ["account"] = WithPrimaryId(BuildNamedMeta("account", accountName), "accountid"),
            ["contact"] = WithPrimaryId(BuildNamedMeta("contact", contactLookup), "contactid"),
        };
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account", "contact"],
            RecordCounts = new() { ["account"] = 3, ["contact"] = 2 },
            FieldRules = new() { ["contact"] = new() { ["parentaccountid"] = new LookupRandomRule() } },
        };

        var (emptySut, emptyCaptured) = BuildSut();
        var emptyError = await Assert.ThrowsAsync<DataGenerationException>(() =>
            emptySut.CreateAsync(config, meta, graph));
        Assert.Contains("contact.parentaccountid", emptyError.Message);
        Assert.Empty(emptyCaptured);

        var (faultSut, faultCaptured) = BuildSut(onQuery: (q, _) =>
        {
            if (string.Equals(q.EntityName, "account", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("permission denied");
            return new EntityCollection();
        });
        var faultError = await Assert.ThrowsAsync<DataGenerationException>(() =>
            faultSut.CreateAsync(config, meta, graph));
        Assert.Contains("contact.parentaccountid", faultError.Message);
        Assert.Contains("account", faultError.Message);
        Assert.Empty(faultCaptured);
    }

    [Fact]
    public async Task Random_lookup_cancellation_stops_before_write()
    {
        using var cts = new CancellationTokenSource();
        var lookup = LookupAttr("parentaccountid", "account");
        var source = WithPrimaryId(BuildNamedMeta("contact", lookup), "contactid");
        var account = WithPrimaryId(new EntityMetadata { LogicalName = "account" }, "accountid");
        var sawQueryToken = false;
        var (sut, captured) = BuildSut(onQuery: (q, ct) =>
        {
            if (!string.Equals(q.EntityName, "account", StringComparison.OrdinalIgnoreCase))
                return new EntityCollection();
            sawQueryToken = ct.CanBeCanceled;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return TargetEntities("account", Guid.Parse("11111111-1111-1111-1111-111111111111"));
        });
        var graph = new DependencyGraph();
        graph.AddNode("contact");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 2 },
            FieldRules = new() { ["contact"] = new() { ["parentaccountid"] = new LookupRandomRule() } },
        };
        var meta = new Dictionary<string, EntityMetadata> { ["contact"] = source, ["account"] = account };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.CreateAsync(config, meta, graph, ct: cts.Token));
        Assert.True(sawQueryToken);
        Assert.Empty(captured);
        Assert.DoesNotContain(captured, e => e.Contains("parentaccountid") && e["parentaccountid"] is null);
    }

    [Fact]
    public async Task Random_customer_combines_available_targets()
    {
        var customer = LookupAttr("customerid", "account", "contact");
        SetAttributeType(customer, AttributeTypeCode.Customer);
        var source = WithPrimaryId(BuildNamedMeta("incident", customer), "incidentid");
        var account = WithPrimaryId(new EntityMetadata { LogicalName = "account" }, "accountid");
        var contact = WithPrimaryId(new EntityMetadata { LogicalName = "contact" }, "contactid");
        var contactId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var meta = new Dictionary<string, EntityMetadata>
        {
            ["incident"] = source, ["account"] = account, ["contact"] = contact,
        };
        var graph = new DependencyGraph();
        graph.AddNode("incident");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["incident"],
            RecordCounts = new() { ["incident"] = 3 },
            FieldRules = new() { ["incident"] = new() { ["customerid"] = new LookupRandomRule() } },
        };

        var (okSut, okCaptured) = BuildSut(onQuery: (q, _) =>
        {
            if (string.Equals(q.EntityName, "contact", StringComparison.OrdinalIgnoreCase))
                return TargetEntities("contact", contactId);
            return new EntityCollection();
        });
        await okSut.CreateAsync(config, meta, graph);
        Assert.Equal(3, okCaptured.Count);
        Assert.All(okCaptured, e =>
        {
            var eref = Assert.IsType<EntityReference>(e["customerid"]);
            Assert.Equal("contact", eref.LogicalName);
            Assert.Equal(contactId, eref.Id);
        });

        var (faultSut, faultCaptured) = BuildSut(onQuery: (q, _) =>
        {
            if (string.Equals(q.EntityName, "account", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("account query failed");
            if (string.Equals(q.EntityName, "contact", StringComparison.OrdinalIgnoreCase))
                return TargetEntities("contact", contactId);
            return new EntityCollection();
        });
        var fault = await Assert.ThrowsAsync<DataGenerationException>(() =>
            faultSut.CreateAsync(config, meta, graph));
        Assert.Contains("incident.customerid", fault.Message);
        Assert.Contains("account", fault.Message);
        Assert.Empty(faultCaptured);
    }

    [Fact]
    public async Task Random_lookup_does_not_leak_candidates_between_runs()
    {
        var lookup = LookupAttr("parentaccountid", "account");
        var source = WithPrimaryId(BuildNamedMeta("contact", lookup), "contactid");
        var account = WithPrimaryId(new EntityMetadata { LogicalName = "account" }, "accountid");
        var firstIds = new[]
        {
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        };
        var secondIds = new[]
        {
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        };
        var current = firstIds;
        var (sut, captured) = BuildSut(onQuery: (q, _) =>
        {
            if (!string.Equals(q.EntityName, "account", StringComparison.OrdinalIgnoreCase))
                return new EntityCollection();
            return TargetEntities("account", current);
        });
        var graph = new DependencyGraph();
        graph.AddNode("contact");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 4 },
            FieldRules = new() { ["contact"] = new() { ["parentaccountid"] = new LookupRandomRule() } },
        };
        var meta = new Dictionary<string, EntityMetadata> { ["contact"] = source, ["account"] = account };

        await sut.CreateAsync(config, meta, graph);
        var firstRefs = captured.Select(e => Assert.IsType<EntityReference>(e["parentaccountid"]).Id).ToArray();
        Assert.All(firstRefs, id => Assert.Contains(id, firstIds));

        captured.Clear();
        current = secondIds;
        await sut.CreateAsync(config, meta, graph);
        var secondRefs = captured.Select(e => Assert.IsType<EntityReference>(e["parentaccountid"]).Id).ToArray();
        Assert.Equal(4, secondRefs.Length);
        Assert.All(secondRefs, id => Assert.Contains(id, secondIds));
        Assert.DoesNotContain(secondRefs, id => firstIds.Contains(id));
    }

    private static async Task<List<EntityReference>> RunOrderedCandidatesAsync(Guid[] serviceOrder)
    {
        var lookup = LookupAttr("parentaccountid", "account");
        var name = NameAttr();
        var source = WithPrimaryId(BuildNamedMeta("contact", lookup, name), "contactid");
        var account = WithPrimaryId(new EntityMetadata { LogicalName = "account" }, "accountid");
        var (sut, captured) = BuildSut(onQuery: (q, _) =>
            string.Equals(q.EntityName, "account", StringComparison.OrdinalIgnoreCase)
                ? TargetEntities("account", serviceOrder)
                : new EntityCollection());
        var graph = new DependencyGraph();
        graph.AddNode("contact");
        await sut.CreateAsync(new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 5 },
            BatchSize = 10,
            MaxParallelism = 1,
            FieldRules = new()
            {
                ["contact"] = new()
                {
                    ["parentaccountid"] = new LookupRandomRule(),
                    ["name"] = new PatternRule("R{seq:0000}"),
                },
            },
        }, new Dictionary<string, EntityMetadata> { ["contact"] = source, ["account"] = account }, graph);
        return [.. captured.OrderBy(e => (string)e["name"])
            .Select(e => Assert.IsType<EntityReference>(e["parentaccountid"]))];
    }

    private static async Task<Dictionary<string, (string Entity, Guid Id)>> RunBatchSizeAsync(
        int batchSize, int parallelism, params Guid[] candidateIds)
    {
        var lookup = LookupAttr("parentaccountid", "account");
        var name = NameAttr();
        var source = WithPrimaryId(BuildNamedMeta("contact", lookup, name), "contactid");
        var account = WithPrimaryId(new EntityMetadata { LogicalName = "account" }, "accountid");
        var (sut, captured) = BuildSut(onQuery: (q, _) =>
            string.Equals(q.EntityName, "account", StringComparison.OrdinalIgnoreCase)
                ? TargetEntities("account", candidateIds)
                : new EntityCollection());
        var graph = new DependencyGraph();
        graph.AddNode("contact");
        await sut.CreateAsync(new GenerationConfig
        {
            EntityLogicalNames = ["contact"],
            RecordCounts = new() { ["contact"] = 8 },
            BatchSize = batchSize,
            MaxParallelism = parallelism,
            FieldRules = new()
            {
                ["contact"] = new()
                {
                    ["parentaccountid"] = new LookupRandomRule(),
                    ["name"] = new PatternRule("R{seq:0000}"),
                },
            },
        }, new Dictionary<string, EntityMetadata> { ["contact"] = source, ["account"] = account }, graph);
        return captured.ToDictionary(
            e => (string)e["name"],
            e =>
            {
                var eref = Assert.IsType<EntityReference>(e["parentaccountid"]);
                return (eref.LogicalName, eref.Id);
            });
    }

    private static LookupAttributeMetadata LookupAttr(string logicalName, params string[] targets) => new()
    {
        LogicalName = logicalName, Targets = targets, IsValidForCreate = true,
    };

    private static StringAttributeMetadata NameAttr() => new()
    {
        LogicalName = "name", MaxLength = 100, IsValidForCreate = true,
    };

    private static EntityMetadata WithPrimaryId(EntityMetadata meta, string primaryId)
    {
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!
            .SetValue(meta, primaryId);
        return meta;
    }

    private static void SetAttributeType(AttributeMetadata attr, AttributeTypeCode type)
        => typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(attr, type);

    private static PagingInfo CopyPageInfo(PagingInfo? info) => new()
    {
        Count = info?.Count ?? 0,
        PageNumber = info?.PageNumber ?? 0,
        PagingCookie = info?.PagingCookie,
        ReturnTotalRecordCount = info?.ReturnTotalRecordCount ?? false,
    };

    private static EntityCollection TargetEntities(string logicalName, params Guid[] ids)
        => TargetEntities(logicalName, (IReadOnlyList<Guid>)ids);

    private static EntityCollection TargetEntities(
        string logicalName, IReadOnlyList<Guid> ids, bool more = false, string? cookie = null)
        => new(ids.Select(id => new Entity(logicalName) { Id = id }).ToList())
        {
            EntityName = logicalName,
            MoreRecords = more,
            PagingCookie = cookie,
        };

    private static IEnumerable<Guid> SequentialIds(int start, int count)
    {
        for (var i = 0; i < count; i++)
            yield return Guid.Parse($"00000000-0000-0000-0000-{(start + i):D12}");
    }
}

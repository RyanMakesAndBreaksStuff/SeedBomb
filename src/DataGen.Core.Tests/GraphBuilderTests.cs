using System.Reflection;

namespace DataGen.Core.Tests;

public class GraphBuilderTests
{
    private readonly GraphBuilder _builder = new(NullLogger<GraphBuilder>.Instance);

    private static EntityMetadata MakeEntity(string logicalName, AttributeMetadata[]? attributes = null,
        ManyToManyRelationshipMetadata[]? n2n = null)
    {
        var e = new EntityMetadata { LogicalName = logicalName };
        if (attributes is not null)
            SetReadOnly(e, "Attributes", attributes);
        if (n2n is not null)
            SetReadOnly(e, "ManyToManyRelationships", n2n);
        return e;
    }

    private static void SetReadOnly(object obj, string propertyName, object value)
    {
        // Try property setter first (public or private)
        var type = obj.GetType();
        while (type is not null)
        {
            var prop = type.GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (prop?.CanWrite == true)
            {
                prop.SetValue(obj, value);
                return;
            }
            // Try backing field at this level of the hierarchy
            var field = type.GetField($"<{propertyName}>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                field.SetValue(obj, value);
                return;
            }
            type = type.BaseType;
        }
    }

    private static LookupAttributeMetadata MakeLookup(string logicalName, string[] targets, bool validForCreate = true)
        => new()
        {
            LogicalName = logicalName,
            IsValidForCreate = validForCreate,
            Targets = targets
        };

    [Fact]
    public void Build_EmptyDictionary_EmptyGraph()
    {
        var result = _builder.Build(new Dictionary<string, EntityMetadata>());
        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Build_NoLookups_AllNodesNoEdges()
    {
        var entities = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = MakeEntity("account", [new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true }]),
            ["contact"] = MakeEntity("contact")
        };
        var graph = _builder.Build(entities);

        Assert.Contains("account", graph.Nodes);
        Assert.Contains("contact", graph.Nodes);
        Assert.Empty(graph.Edges["account"]);
        Assert.Empty(graph.Edges["contact"]);
    }

    [Fact]
    public void Build_LookupBetweenSelectedEntities_CreatesEdge()
    {
        var entities = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = MakeEntity("contact", [MakeLookup("accountid", ["account"])]),
            ["account"] = MakeEntity("account")
        };
        var graph = _builder.Build(entities);

        Assert.Contains("account", graph.Edges["contact"]);
    }

    [Fact]
    public void Build_LookupToOutOfScopeEntity_EdgeNotAdded()
    {
        var entities = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = MakeEntity("contact", [MakeLookup("originatingleadid", ["lead"])])
        };
        var graph = _builder.Build(entities);

        Assert.Empty(graph.Edges["contact"]);
    }

    [Fact]
    public void Build_SelfTargetingLookup_NotAddedAsEdge()
    {
        var entities = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = MakeEntity("account", [MakeLookup("parentaccountid", ["account"])])
        };
        var graph = _builder.Build(entities);

        Assert.Empty(graph.Edges["account"]);
    }

    [Fact]
    public void Build_NotValidForCreateLookup_EdgeNotAdded()
    {
        var entities = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = MakeEntity("contact", [MakeLookup("accountid", ["account"], validForCreate: false)]),
            ["account"] = MakeEntity("account")
        };
        var graph = _builder.Build(entities);

        Assert.Empty(graph.Edges["contact"]);
    }

    [Fact]
    public void Build_NoManyToManyRelationshipsInMetadata_EmptyRelationships()
    {
        // Note: EntityMetadata.ManyToManyRelationships is backed by SDK-internal storage that
        // cannot be set via reflection in tests. N:N relationship processing is covered by
        // DependencyGraph.AddRelationship tests. This test verifies the builder handles
        // entities with no N:N metadata gracefully.
        var entities = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = MakeEntity("contact"),
            ["product"] = MakeEntity("product")
        };
        var graph = _builder.Build(entities);

        Assert.Empty(graph.Relationships);
    }

    [Fact]
    public void Build_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => _builder.Build(null!));
    }

    [Fact]
    public void Explicit_lookup_removes_only_its_own_graph_contribution()
    {
        var entities = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = MakeEntity("contact", [MakeLookup("accountid", ["account"])]),
            ["account"] = MakeEntity("account"),
        };

        bool RuledOnly(string table, string column) =>
            string.Equals(table, "contact", StringComparison.OrdinalIgnoreCase)
            && string.Equals(column, "accountid", StringComparison.OrdinalIgnoreCase);

        var ruledOnly = _builder.Build(entities, RuledOnly);
        Assert.DoesNotContain("account", ruledOnly.Edges["contact"]);

        entities["contact"] = MakeEntity("contact",
        [
            MakeLookup("accountid", ["account"]),
            MakeLookup("parentaccountid", ["account"]),
        ]);
        var withUnruledSibling = _builder.Build(entities, RuledOnly);
        Assert.Contains("account", withUnruledSibling.Edges["contact"]);
    }
}

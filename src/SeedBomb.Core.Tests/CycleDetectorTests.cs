using System.Reflection;

namespace SeedBomb.Core.Tests;

public class CycleDetectorTests
{
    private readonly CycleDetector _detector = new(NullLogger<CycleDetector>.Instance);

    private static void SetReadOnlyProperty(object obj, string propertyName, object value)
    {
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

    private static EntityMetadata MakeEntityWithLookup(string logicalName, string lookupField, string[] targets,
        AttributeRequiredLevel requiredLevel = AttributeRequiredLevel.None)
    {
        var entity = new EntityMetadata { LogicalName = logicalName };
        var lookup = new LookupAttributeMetadata
        {
            LogicalName = lookupField,
            IsValidForCreate = true,
            Targets = targets,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(requiredLevel)
        };
        SetReadOnlyProperty(entity, "Attributes", new AttributeMetadata[] { lookup });
        return entity;
    }

    [Fact]
    public void FindStronglyConnectedComponents_AcyclicGraph_ReturnsEmpty()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");

        var sccs = _detector.FindStronglyConnectedComponents(graph);
        Assert.Empty(sccs);
    }

    [Fact]
    public void FindStronglyConnectedComponents_SimpleCycle_DetectsBothNodes()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        graph.AddEdge("account", "contact");

        var sccs = _detector.FindStronglyConnectedComponents(graph);
        Assert.Single(sccs);
        Assert.Equal(2, sccs[0].Count);
        Assert.Contains("contact", sccs[0]);
        Assert.Contains("account", sccs[0]);
    }

    [Fact]
    public void FindStronglyConnectedComponents_ThreeNodeCycle_DetectsAll()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("a", "b");
        graph.AddEdge("b", "c");
        graph.AddEdge("c", "a");

        var sccs = _detector.FindStronglyConnectedComponents(graph);
        Assert.Single(sccs);
        Assert.Equal(3, sccs[0].Count);
    }

    [Fact]
    public void FindStronglyConnectedComponents_MultipleDisconnectedCycles_DetectsBoth()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("a", "b");
        graph.AddEdge("b", "a");
        graph.AddEdge("c", "d");
        graph.AddEdge("d", "c");

        var sccs = _detector.FindStronglyConnectedComponents(graph);
        Assert.Equal(2, sccs.Count);
    }

    [Fact]
    public void FindStronglyConnectedComponents_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => _detector.FindStronglyConnectedComponents(null!));
    }

    [Fact]
    public void BreakCycles_OptionalLookup_DeferredSuccessfully()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        graph.AddEdge("account", "contact");

        var entityMetadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = MakeEntityWithLookup("contact", "accountid", ["account"], AttributeRequiredLevel.None),
            ["account"] = MakeEntityWithLookup("account", "primarycontactid", ["contact"], AttributeRequiredLevel.None)
        };

        var cycles = _detector.FindStronglyConnectedComponents(graph);
        _detector.BreakCycles(graph, cycles, entityMetadata);

        Assert.NotEmpty(graph.DeferredEdges);
    }

    [Fact]
    public void BreakCycles_AllSystemRequired_ThrowsUnbreakableCycleException()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("a", "b");
        graph.AddEdge("b", "a");

        var entityMetadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = MakeEntityWithLookup("a", "bid", ["b"], AttributeRequiredLevel.SystemRequired),
            ["b"] = MakeEntityWithLookup("b", "aid", ["a"], AttributeRequiredLevel.SystemRequired)
        };

        var cycles = _detector.FindStronglyConnectedComponents(graph);
        Assert.Throws<UnbreakableCycleException>(() =>
            _detector.BreakCycles(graph, cycles, entityMetadata));
    }

    [Fact]
    public void BreakCycles_ThrowsOnNulls()
    {
        var graph = new DependencyGraph();
        Assert.Throws<ArgumentNullException>(() =>
            _detector.BreakCycles(null!, [], new Dictionary<string, EntityMetadata>()));
        Assert.Throws<ArgumentNullException>(() =>
            _detector.BreakCycles(graph, null!, new Dictionary<string, EntityMetadata>()));
        Assert.Throws<ArgumentNullException>(() =>
            _detector.BreakCycles(graph, [], null!));
    }

    [Fact]
    public void Cycle_breaking_never_defers_a_ruled_sibling()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        graph.AddEdge("account", "contact");

        var contact = new EntityMetadata { LogicalName = "contact" };
        SetReadOnlyProperty(contact, "Attributes", new AttributeMetadata[]
        {
            new LookupAttributeMetadata
            {
                LogicalName = "ruledaccountid",
                IsValidForCreate = true,
                Targets = ["account"],
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
            new LookupAttributeMetadata
            {
                LogicalName = "unruledaccountid",
                IsValidForCreate = true,
                Targets = ["account"],
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });

        var entityMetadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["contact"] = contact,
            ["account"] = MakeEntityWithLookup("account", "primarycontactid", ["contact"],
                AttributeRequiredLevel.SystemRequired),
        };

        var cycles = _detector.FindStronglyConnectedComponents(graph);
        _detector.BreakCycles(graph, cycles, entityMetadata,
            (table, column) => string.Equals(table, "contact", StringComparison.OrdinalIgnoreCase)
                && string.Equals(column, "ruledaccountid", StringComparison.OrdinalIgnoreCase));

        var deferred = Assert.Single(graph.DeferredEdges["contact"]);
        Assert.Equal("unruledaccountid", deferred.FieldLogicalName);
        Assert.DoesNotContain(graph.DeferredEdges.SelectMany(p => p.Value),
            d => string.Equals(d.FieldLogicalName, "ruledaccountid", StringComparison.OrdinalIgnoreCase));
    }
}

namespace DataGen.Core.Tests;

public class DependencyGraphTests
{
    [Fact]
    public void AddNode_NewNode_AppearsInNodes()
    {
        var graph = new DependencyGraph();
        graph.AddNode("account");
        Assert.Contains("account", graph.Nodes);
    }

    [Fact]
    public void AddNode_Duplicate_NoException()
    {
        var graph = new DependencyGraph();
        graph.AddNode("account");
        graph.AddNode("account"); // should not throw
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public void AddEdge_AddsSourceAndTargetAsNodes()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        Assert.Contains("contact", graph.Nodes);
        Assert.Contains("account", graph.Nodes);
    }

    [Fact]
    public void AddEdge_CreatesDirectedEdge()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        Assert.Contains("account", graph.Edges["contact"]);
        Assert.DoesNotContain("contact", graph.Edges.GetValueOrDefault("account", []));
    }

    [Fact]
    public void DeferEdge_RemovesEdgeAndRecordsDeferred()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account");
        var deferred = new DeferredLookup("contact", "accountid", ["account"]);
        graph.DeferEdge("contact", deferred);

        Assert.DoesNotContain("account", graph.Edges["contact"]);
        Assert.Contains(deferred, graph.DeferredEdges["contact"]);
    }

    [Fact]
    public void AddRelationship_AppearsOnBothEntities()
    {
        var graph = new DependencyGraph();
        var rel = new ManyToManyRelationship("schema_name", "contact", "product");
        graph.AddRelationship(rel);

        Assert.Contains(rel, graph.Relationships["contact"]);
        Assert.Contains(rel, graph.Relationships["product"]);
    }

    [Fact]
    public void AddRelationship_SelfRelationship_AddedOnce()
    {
        var graph = new DependencyGraph();
        var rel = new ManyToManyRelationship("schema_name", "account", "account");
        graph.AddRelationship(rel);

        Assert.Single(graph.Relationships["account"]);
    }

    [Fact]
    public void Nodes_InitiallyEmpty()
    {
        var graph = new DependencyGraph();
        Assert.Empty(graph.Nodes);
    }

    [Fact]
    public void DeferredEdges_InitiallyEmpty()
    {
        var graph = new DependencyGraph();
        Assert.Empty(graph.DeferredEdges);
    }

    [Fact]
    public void AddNode_DoesNotCreateSelfEdge()
    {
        var graph = new DependencyGraph();
        graph.AddNode("account");
        Assert.Empty(graph.Edges["account"]);
    }
}

namespace SeedBomb.Core.Tests;

public class TopologicalSortTests
{
    private readonly TopologicalSort _sort = new(NullLogger<TopologicalSort>.Instance);

    [Fact]
    public void Sort_SingleNode_ReturnsSingleElement()
    {
        var graph = new DependencyGraph();
        graph.AddNode("account");

        var result = _sort.Sort(graph);
        Assert.Single(result);
        Assert.Equal("account", result[0]);
    }

    [Fact]
    public void Sort_AcyclicGraph_DependencyBeforeDependent()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("contact", "account"); // contact depends on account

        var list = _sort.Sort(graph).ToList();
        Assert.Equal(2, list.Count);
        Assert.True(list.IndexOf("account") < list.IndexOf("contact"),
            "account must appear before contact");
    }

    [Fact]
    public void Sort_DiamondDependency_ValidOrder()
    {
        var graph = new DependencyGraph();
        // c → a, c → b, a → root, b → root
        graph.AddEdge("c", "a");
        graph.AddEdge("c", "b");
        graph.AddEdge("a", "root");
        graph.AddEdge("b", "root");

        var list = _sort.Sort(graph).ToList();
        Assert.Equal(4, list.Count);
        Assert.True(list.IndexOf("root") < list.IndexOf("a"));
        Assert.True(list.IndexOf("root") < list.IndexOf("b"));
        Assert.True(list.IndexOf("a") < list.IndexOf("c"));
        Assert.True(list.IndexOf("b") < list.IndexOf("c"));
    }

    [Fact]
    public void Sort_EmptyGraph_ReturnsEmpty()
    {
        var graph = new DependencyGraph();
        var result = _sort.Sort(graph);
        Assert.Empty(result);
    }

    [Fact]
    public void Sort_RemainingCycle_ThrowsCyclicalDependencyException()
    {
        var graph = new DependencyGraph();
        graph.AddEdge("a", "b");
        graph.AddEdge("b", "a");

        Assert.Throws<CyclicalDependencyException>(() => _sort.Sort(graph));
    }

    [Fact]
    public void Sort_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => _sort.Sort(null!));
    }
}

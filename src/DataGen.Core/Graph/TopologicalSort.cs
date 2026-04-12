using DataGen.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace DataGen.Core.Graph;

/// <summary>
/// Produces a topological ordering of entities using Kahn's algorithm.
/// Entities with no dependencies (in-degree 0) sort first, ensuring that
/// when an entity is created, all its lookup targets already exist.
/// Time complexity: O(V+E).
/// </summary>
public class TopologicalSort
{
    private readonly ILogger<TopologicalSort> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TopologicalSort"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public TopologicalSort(ILogger<TopologicalSort> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns a topological ordering of entities. Entities with no incoming edges appear first.
    /// </summary>
    /// <param name="graph">The dependency graph (should be acyclic after cycle-breaking).</param>
    /// <returns>An ordered list of entity logical names.</returns>
    /// <exception cref="CyclicalDependencyException">Thrown if unresolved cycles remain.</exception>
    public IReadOnlyList<string> Sort(DependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Initialize in-degree for all nodes
        foreach (var node in graph.Nodes)
        {
            inDegree[node] = 0;
        }

        // Calculate in-degrees from edges
        foreach (var (source, targets) in graph.Edges)
        {
            foreach (var target in targets)
            {
                if (inDegree.ContainsKey(target))
                {
                    inDegree[target]++;
                }
            }
        }

        // Seed queue with nodes having in-degree 0 (no dependencies)
        var queue = new Queue<string>(
            inDegree.Where(kvp => kvp.Value == 0)
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => kvp.Key));

        var sorted = new List<string>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            if (graph.Edges.TryGetValue(current, out var targets))
            {
                foreach (var target in targets.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
                {
                    if (--inDegree[target] == 0)
                    {
                        queue.Enqueue(target);
                    }
                }
            }
        }

        if (sorted.Count != graph.Nodes.Count)
        {
            var remaining = graph.Nodes.Except(sorted).ToList();
            _logger.LogError("Topological sort failed: {RemainingCount} nodes have unresolved cycles: [{Remaining}]",
                remaining.Count, string.Join(", ", remaining));

            throw new CyclicalDependencyException(
                $"Unresolved cycles remain after SCC break. {remaining.Count} entities could not be sorted: {string.Join(", ", remaining)}",
                [remaining.AsReadOnly()]);
        }

        _logger.LogDebug("Topological sort order: {SortedOrder}", string.Join(" → ", sorted));
        return sorted.AsReadOnly();
    }
}

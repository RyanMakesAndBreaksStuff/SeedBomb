using SeedBomb.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace SeedBomb.Core.Graph;

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
    /// Returns a topological ordering of entities such that every entity's lookup targets
    /// (dependencies) appear before the entity itself. Entities with no dependencies sort first.
    /// Time complexity: O(V+E).
    /// </summary>
    /// <remarks>
    /// Graph edges go FROM the dependent entity TO its dependency
    /// (e.g. contact → account means "contact depends on account").
    /// The sort uses the dependency count (out-degree) as the queue key so that
    /// entities with zero unresolved dependencies are emitted first.
    /// </remarks>
    /// <param name="graph">The dependency graph (should be acyclic after cycle-breaking).</param>
    /// <returns>An ordered list of entity logical names, dependencies before dependents.</returns>
    /// <exception cref="CyclicalDependencyException">Thrown if unresolved cycles remain.</exception>
    public IReadOnlyList<string> Sort(DependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // dependencyCount[X] = number of entities X still needs to wait for before it can be created.
        // Edges are stored as source → target where source depends on target.
        // So dependencyCount = number of outgoing edges per node.
        var dependencyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // dependents[X] = entities that depend on X (i.e. X must be created before these).
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Nodes)
        {
            dependencyCount[node] = 0;
            dependents[node] = [];
        }

        foreach (var (source, targets) in graph.Edges)
        {
            foreach (var target in targets)
            {
                if (!dependencyCount.ContainsKey(source)) continue;

                dependencyCount[source]++;

                if (!dependents.ContainsKey(target))
                    dependents[target] = [];
                dependents[target].Add(source);
            }
        }

        // Seed queue with nodes that have zero dependencies — they can be created immediately.
        var queue = new Queue<string>(
            dependencyCount.Where(kvp => kvp.Value == 0)
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => kvp.Key));

        var sorted = new List<string>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            // current is now created; all entities that depended on it have one fewer blocker.
            foreach (var dependent in dependents[current].OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                if (--dependencyCount[dependent] == 0)
                {
                    queue.Enqueue(dependent);
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

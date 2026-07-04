using DataGen.Core.Contracts;
using DataGen.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Graph;

/// <summary>
/// Detects strongly connected components (cycles) in the dependency graph
/// using Tarjan's algorithm and breaks them by deferring optional lookup edges.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="CycleDetector"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
public class CycleDetector(ILogger<CycleDetector> logger)
{
    private readonly ILogger<CycleDetector> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Finds all strongly connected components with more than one node using Tarjan's SCC algorithm.
    /// Time complexity: O(V+E).
    /// </summary>
    /// <param name="graph">The dependency graph to analyze.</param>
    /// <returns>A list of SCCs, each containing the entity logical names in the cycle.</returns>
    public IReadOnlyList<IReadOnlyList<string>> FindStronglyConnectedComponents(DependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowLinks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IReadOnlyList<string>>();

        void StrongConnect(string node)
        {
            indices[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            if (graph.Edges.TryGetValue(node, out var targets))
            {
                foreach (var target in targets)
                {
                    if (!indices.TryGetValue(target, out int value))
                    {
                        StrongConnect(target);
                        lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                    }
                    else if (onStack.Contains(target))
                    {
                        lowLinks[node] = Math.Min(lowLinks[node], value);
                    }
                }
            }

            if (lowLinks[node] == indices[node])
            {
                var component = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    component.Add(w);
                } while (!string.Equals(w, node, StringComparison.OrdinalIgnoreCase));

                // Only report SCCs with more than one node (actual cycles)
                if (component.Count > 1)
                {
                    result.Add(component.AsReadOnly());
                }
            }
        }

        foreach (var node in graph.Nodes)
        {
            if (!indices.ContainsKey(node))
            {
                StrongConnect(node);
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Found {CycleCount} cycles in dependency graph", result.Count);
        }
        return result.AsReadOnly();
    }

    /// <summary>
    /// Breaks cycles by deferring optional lookup edges. For each cycle, finds an edge where
    /// the lookup field is not <c>SystemRequired</c> and moves it to deferred edges.
    /// If all edges in a cycle are SystemRequired, throws <see cref="UnbreakableCycleException"/>.
    /// </summary>
    /// <param name="graph">The dependency graph to modify.</param>
    /// <param name="cycles">The SCCs to break.</param>
    /// <param name="entityMetadata">Metadata for looking up field required levels.</param>
    public void BreakCycles(
        DependencyGraph graph,
        IReadOnlyList<IReadOnlyList<string>> cycles,
        IReadOnlyDictionary<string, EntityMetadata> entityMetadata)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(entityMetadata);

        var unbreakable = new List<IReadOnlyList<string>>();

        foreach (var cycle in cycles)
        {
            var cycleSet = new HashSet<string>(cycle, StringComparer.OrdinalIgnoreCase);
            var broken = false;

            foreach (var entity in cycle)
            {
                if (broken)
                    break;

                if (!graph.Edges.TryGetValue(entity, out var targets))
                    continue;

                if (!entityMetadata.TryGetValue(entity, out var meta) || meta.Attributes is null)
                    continue;

                var lookups = meta.Attributes
                    .OfType<LookupAttributeMetadata>()
                    .Where(l => l.IsValidForCreate == true && l.Targets is not null);

                foreach (var lookup in lookups)
                {
                    var cycleTargets = lookup.Targets!
                        .Where(t => cycleSet.Contains(t) && targets.Contains(t))
                        .ToArray();

                    if (cycleTargets.Length == 0)
                        continue;

                    // Only defer if NOT SystemRequired (value 1)
                    var requiredLevel = lookup.RequiredLevel?.Value;
                    if (requiredLevel == AttributeRequiredLevel.SystemRequired)
                        continue;

                    var deferred = new DeferredLookup(entity, lookup.LogicalName, cycleTargets);
                    graph.DeferEdge(entity, deferred);

                    _logger.LogWarning(
                        "Broke cycle by deferring {Entity}.{Field} → [{Targets}]",
                        entity, lookup.LogicalName, string.Join(", ", cycleTargets));

                    broken = true;
                    break;
                }
            }

            if (!broken)
            {
                _logger.LogError(
                    "Cannot break cycle [{Entities}] — all edges are SystemRequired",
                    string.Join(" → ", cycle));
                unbreakable.Add(cycle);
            }
        }

        if (unbreakable.Count > 0)
        {
            throw new UnbreakableCycleException(
                $"Found {unbreakable.Count} cycle(s) that cannot be broken because all lookup edges are SystemRequired.",
                unbreakable);
        }
    }
}

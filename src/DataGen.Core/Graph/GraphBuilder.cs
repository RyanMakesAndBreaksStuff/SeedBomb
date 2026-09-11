using DataGen.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Graph;

/// <summary>
/// Builds a <see cref="DependencyGraph"/> from Dataverse entity metadata by
/// analyzing lookup relationships between selected entities.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="GraphBuilder"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
public class GraphBuilder(ILogger<GraphBuilder> logger)
{
    private readonly ILogger<GraphBuilder> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Builds a dependency graph from the given entity metadata.
    /// Lookup fields create edges from the source entity to each target entity.
    /// Self-referential lookups are flagged separately and are NOT treated as cycles.
    /// </summary>
    /// <param name="selectedEntities">The entities to include, keyed by logical name.</param>
    /// <param name="isExplicitLookup">
    /// Optional predicate identifying lookup columns that supply their own value or omission
    /// and must not contribute graph edges. Arguments are source table and column logical names.
    /// </param>
    /// <returns>The constructed dependency graph.</returns>
    public DependencyGraph Build(
        IReadOnlyDictionary<string, EntityMetadata> selectedEntities,
        Func<string, string, bool>? isExplicitLookup = null)
    {
        ArgumentNullException.ThrowIfNull(selectedEntities);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Building dependency graph for {EntityCount} entities", selectedEntities.Count);
        }

        var graph = new DependencyGraph();

        // Add all selected entities as nodes
        foreach (var entityName in selectedEntities.Keys)
        {
            graph.AddNode(entityName);
        }

        foreach (var (entityLogicalName, entityMeta) in selectedEntities)
        {
            if (entityMeta.Attributes is null)
                continue;

            var lookups = entityMeta.Attributes
                .OfType<LookupAttributeMetadata>()
                .Where(l => l.IsValidForCreate == true);

            foreach (var lookup in lookups)
            {
                if (lookup.LogicalName is not null
                    && isExplicitLookup?.Invoke(entityLogicalName, lookup.LogicalName) == true)
                    continue;

                if (lookup.Targets is null || lookup.Targets.Length == 0)
                    continue;

                foreach (var target in lookup.Targets)
                {
                    if (!selectedEntities.ContainsKey(target))
                        continue;

                    if (string.Equals(target, entityLogicalName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Self-referential — NOT a cycle
                        graph.AddSelfReference(entityLogicalName);
                        _logger.LogDebug("Self-reference detected: {Entity}.{Field}",
                            entityLogicalName, lookup.LogicalName);
                    }
                    else
                    {
                        graph.AddEdge(entityLogicalName, target);
                        _logger.LogDebug("Edge added: {Source}.{Field} → {Target}",
                            entityLogicalName, lookup.LogicalName, target);
                    }
                }
            }

            // Process many-to-many relationships
            if (entityMeta.ManyToManyRelationships is not null)
            {
                foreach (var rel in entityMeta.ManyToManyRelationships)
                {
                    var entity1 = rel.Entity1LogicalName;
                    var entity2 = rel.Entity2LogicalName;

                    if (selectedEntities.ContainsKey(entity1) && selectedEntities.ContainsKey(entity2))
                    {
                        graph.AddRelationship(new ManyToManyRelationship(
                            rel.SchemaName, entity1, entity2));

                        _logger.LogDebug("N:N relationship: {Schema} ({Entity1} ↔ {Entity2})",
                            rel.SchemaName, entity1, entity2);
                    }
                }
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var nodeCount = graph.Nodes.Count;
            var edgeCount = graph.Edges.Values.Sum(e => e.Count);
            var selfRefCount = graph.SelfReferences.Count;

            _logger.LogInformation(
                "Dependency graph built: {NodeCount} nodes, {EdgeCount} edges, {SelfRefCount} self-references",
                nodeCount,
                edgeCount,
                selfRefCount);
        }

        return graph;
    }
}

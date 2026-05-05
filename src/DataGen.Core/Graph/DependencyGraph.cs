using DataGen.Core.Contracts;

namespace DataGen.Core.Graph;

/// <summary>
/// Represents the dependency relationships between Dataverse entities based on lookup fields.
/// Edges point from a source entity to its target (dependency) entity.
/// </summary>
public class DependencyGraph
{
    private readonly Dictionary<string, HashSet<string>> _edges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DeferredLookup>> _deferredEdges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selfReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ManyToManyRelationship>> _relationships = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nodes = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, IReadOnlyList<DeferredLookup>>? _deferredEdgesCache;
    private IReadOnlyDictionary<string, IReadOnlyList<ManyToManyRelationship>>? _relationshipsCache;

    /// <summary>
    /// Gets the direct dependency edges. Key = source entity, Value = set of target entities.
    /// </summary>
    public IReadOnlyDictionary<string, HashSet<string>> Edges => _edges;

    /// <summary>
    /// Gets the deferred edges that were removed during cycle-breaking. These lookups
    /// must be backfilled after initial record creation.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DeferredLookup>> DeferredEdges =>
        _deferredEdgesCache ??= _deferredEdges.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<DeferredLookup>)kvp.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets entities that reference themselves (e.g., parentaccountid on Account).
    /// These are NOT cycles and are handled via hierarchical generation.
    /// </summary>
    public IReadOnlySet<string> SelfReferences => _selfReferences;

    /// <summary>
    /// Gets many-to-many relationships keyed by entity logical name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ManyToManyRelationship>> Relationships =>
        _relationshipsCache ??= _relationships.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<ManyToManyRelationship>)kvp.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets all entity nodes in the graph.
    /// </summary>
    public IReadOnlySet<string> Nodes => _nodes;

    /// <summary>
    /// Adds a node (entity) to the graph.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    public void AddNode(string entityLogicalName)
    {
        _nodes.Add(entityLogicalName);
        _edges.TryAdd(entityLogicalName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds a directed edge from source to target indicating a lookup dependency.
    /// </summary>
    /// <param name="source">The source entity (the one containing the lookup field).</param>
    /// <param name="target">The target entity (the one being referenced).</param>
    public void AddEdge(string source, string target)
    {
        AddNode(source);
        AddNode(target);
        _edges[source].Add(target);
    }

    /// <summary>
    /// Removes an edge and records it as a deferred lookup for post-creation backfill.
    /// </summary>
    /// <param name="source">The source entity.</param>
    /// <param name="deferredLookup">The deferred lookup details.</param>
    public void DeferEdge(string source, DeferredLookup deferredLookup)
    {
        if (_edges.TryGetValue(source, out var targets))
        {
            foreach (var target in deferredLookup.TargetEntities)
            {
                targets.Remove(target);
            }
        }

        if (!_deferredEdges.TryGetValue(source, out var deferred))
        {
            deferred = [];
            _deferredEdges[source] = deferred;
        }
        deferred.Add(deferredLookup);
        _deferredEdgesCache = null;
    }

    /// <summary>
    /// Marks an entity as self-referencing.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    public void AddSelfReference(string entityLogicalName)
    {
        _selfReferences.Add(entityLogicalName);
    }

    /// <summary>
    /// Adds a many-to-many relationship.
    /// </summary>
    /// <param name="relationship">The relationship details.</param>
    public void AddRelationship(ManyToManyRelationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        AddRelationshipForEntity(relationship.Entity1LogicalName, relationship);
        if (!string.Equals(relationship.Entity1LogicalName, relationship.Entity2LogicalName, StringComparison.OrdinalIgnoreCase))
        {
            AddRelationshipForEntity(relationship.Entity2LogicalName, relationship);
        }
    }

    private void AddRelationshipForEntity(string entityLogicalName, ManyToManyRelationship relationship)
    {
        if (!_relationships.TryGetValue(entityLogicalName, out var list))
        {
            list = [];
            _relationships[entityLogicalName] = list;
        }
        list.Add(relationship);
        _relationshipsCache = null;
    }
}

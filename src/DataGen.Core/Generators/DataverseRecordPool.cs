using Bogus;
using System.Collections.Concurrent;

namespace DataGen.Core.Generators;

/// <summary>
/// Thread-safe pool of created record IDs, used by generators to resolve lookups
/// to previously created records.
/// </summary>
public class DataverseRecordPool
{
    private readonly ConcurrentDictionary<string, List<Guid>> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lockObj = new();

    /// <summary>
    /// Adds a batch of record IDs for the given entity.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <param name="ids">The record IDs to add.</param>
    public void Add(string entityLogicalName, IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(entityLogicalName);
        ArgumentNullException.ThrowIfNull(ids);

        var list = _records.GetOrAdd(entityLogicalName, _ => []);
        lock (_lockObj)
        {
            list.AddRange(ids);
        }
    }

    /// <summary>
    /// Gets all record IDs for the given entity.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <returns>The record IDs, or an empty list if none exist.</returns>
    public IReadOnlyList<Guid> Get(string entityLogicalName)
    {
        ArgumentNullException.ThrowIfNull(entityLogicalName);

        if (_records.TryGetValue(entityLogicalName, out var list))
        {
            lock (_lockObj)
            {
                return list.ToList().AsReadOnly();
            }
        }

        return [];
    }

    /// <summary>
    /// Gets a random record ID for the given entity using the provided Faker for deterministic selection.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <param name="faker">The Faker instance for deterministic random selection.</param>
    /// <returns>A random record ID, or null if no records exist for the entity.</returns>
    public Guid? GetRandom(string entityLogicalName, Faker faker)
    {
        ArgumentNullException.ThrowIfNull(entityLogicalName);
        ArgumentNullException.ThrowIfNull(faker);

        if (_records.TryGetValue(entityLogicalName, out var list))
        {
            lock (_lockObj)
            {
                if (list.Count == 0)
                    return null;

                var index = faker.Random.Int(0, list.Count - 1);
                return list[index];
            }
        }

        return null;
    }
}

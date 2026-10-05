using CommunityToolkit.Mvvm.ComponentModel;
using SeedBomb.Core.Contracts;
using System.Collections.ObjectModel;

namespace SeedBomb.ViewModels.Controls;

/// <summary>Represents a single entity's record-count entry in <see cref="FieldOverridesViewModel"/>.</summary>
public sealed partial class EntityCountEntry : ObservableObject
{
    /// <summary>The entity this entry belongs to.</summary>
    public EntitySummary Entity { get; }

    [ObservableProperty] private int _count;

    /// <summary>Initialises the entry.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="count">Initial record count.</param>
    public EntityCountEntry(EntitySummary entity, int count = GenerationLimits.DefaultRecordCount)
    {
        Entity = entity;
        _count = count;
    }
}

/// <summary>ViewModel for <see cref="SeedBomb.Views.Controls.FieldOverridesControl"/>.</summary>
public sealed class FieldOverridesViewModel : ObservableObject
{
    /// <summary>One entry per selected entity.</summary>
    public ObservableCollection<EntityCountEntry> Entries { get; } = [];

    /// <summary>
    /// Updates the entry list to match <paramref name="entities"/>.
    /// Existing counts are preserved; entries for deselected entities are removed.
    /// </summary>
    /// <param name="entities">Currently selected entities.</param>
    /// <param name="defaultCount">Initial count for newly-added entries (the user's configured default record count).</param>
    public void SetEntities(IReadOnlyList<EntitySummary> entities, int defaultCount = GenerationLimits.DefaultRecordCount)
    {
        var existing = Entries.ToDictionary(e => e.Entity.LogicalName, e => e.Count);

        Entries.Clear();
        foreach (var entity in entities)
        {
            var count = existing.GetValueOrDefault(entity.LogicalName, defaultCount);
            Entries.Add(new EntityCountEntry(entity, count));
        }
    }

    /// <summary>Returns the configured record count keyed by entity logical name.</summary>
    public IReadOnlyDictionary<string, int> GetCounts() =>
        Entries.ToDictionary(e => e.Entity.LogicalName, e => e.Count);

    /// <summary>Sets the count for a selected entity (profile Load/Import). No-op if unknown.</summary>
    public void SetCount(string logicalName, int count)
    {
        var entry = Entries.FirstOrDefault(e =>
            string.Equals(e.Entity.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
            entry.Count = count;
    }
}

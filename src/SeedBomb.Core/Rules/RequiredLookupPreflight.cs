using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Rules;

/// <summary>
/// T-25: a SystemRequired lookup has no legacy generator, so Dataverse rejects a create unless a
/// constant, one-of or lookupRandom rule supplies it. Needs only metadata and rules, so Bulk runs
/// it before the first write and Review runs it before Start (CR-003). A lookup whose every target
/// is a table this run creates first needs no rule: the sort never defers a SystemRequired edge, and
/// the in-run pool fills it.
/// </summary>
public static class RequiredLookupPreflight
{
    /// <summary>One message per SystemRequired lookup on <paramref name="meta"/> that no rule supplies; empty when the table can be created.</summary>
    /// <param name="table">Entity logical name used in the messages.</param>
    /// <param name="meta">Live entity metadata.</param>
    /// <param name="rules">The table's validated rules, keyed case-insensitively; null when it has none.</param>
    /// <param name="createdEarlier">Tables this run creates before <paramref name="table"/>; null when none.</param>
    public static IReadOnlyList<string> FindUnsupplied(
        string table,
        EntityMetadata meta,
        IReadOnlyDictionary<string, FieldRule>? rules,
        IReadOnlyCollection<string>? createdEarlier = null)
    {
        ArgumentNullException.ThrowIfNull(meta);

        return UnruledRequiredLookups(meta, rules)
            .Where(attr => !IsCreatedEarlier(table, attr, createdEarlier))
            .Select(attr =>
                $"Entity '{table}': required lookup '{attr.LogicalName}' (SystemRequired) has no generator — add a constant, one-of, or random-lookup rule for it.")
            .ToArray();
    }

    /// <summary>
    /// Logical names of the SystemRequired lookups on <paramref name="meta"/> that <see cref="FindUnsupplied"/>
    /// accepts only because every target is in <paramref name="createdEarlier"/>. The column filter never
    /// generates a SystemRequired lookup, so the caller must give each one a lookupRandom rule itself.
    /// </summary>
    /// <param name="table">Entity logical name.</param>
    /// <param name="meta">Live entity metadata.</param>
    /// <param name="rules">The table's validated rules, keyed case-insensitively; null when it has none.</param>
    /// <param name="createdEarlier">Tables this run creates before <paramref name="table"/>; null when none.</param>
    public static IReadOnlyList<string> FindSuppliedByCreatedEarlier(
        string table,
        EntityMetadata meta,
        IReadOnlyDictionary<string, FieldRule>? rules,
        IReadOnlyCollection<string>? createdEarlier)
    {
        ArgumentNullException.ThrowIfNull(meta);

        return UnruledRequiredLookups(meta, rules)
            .Where(attr => IsCreatedEarlier(table, attr, createdEarlier))
            .Select(attr => attr.LogicalName!)
            .ToArray();
    }

    private static IEnumerable<LookupAttributeMetadata> UnruledRequiredLookups(
        EntityMetadata meta, IReadOnlyDictionary<string, FieldRule>? rules)
        => (meta.Attributes ?? []).OfType<LookupAttributeMetadata>().Where(attr =>
            attr.LogicalName is not null
            && attr.RequiredLevel?.Value == AttributeRequiredLevel.SystemRequired
            // ownerid is SystemRequired on every table; Dataverse defaults it to the calling user.
            && !string.Equals(attr.LogicalName, "ownerid", StringComparison.OrdinalIgnoreCase)
            && !(rules is not null && rules.TryGetValue(attr.LogicalName, out var rule)
                && rule is ConstantRule or OneOfRule or LookupRandomRule));

    // A self-reference has an empty pool at create, so it never counts as created earlier.
    private static bool IsCreatedEarlier(
        string table, LookupAttributeMetadata attr, IReadOnlyCollection<string>? createdEarlier)
    {
        var targets = attr.Targets;
        return createdEarlier is { Count: > 0 }
            && targets is { Length: > 0 }
            && targets.All(t =>
                !string.Equals(t, table, StringComparison.OrdinalIgnoreCase)
                && createdEarlier.Contains(t, StringComparer.OrdinalIgnoreCase));
    }
}

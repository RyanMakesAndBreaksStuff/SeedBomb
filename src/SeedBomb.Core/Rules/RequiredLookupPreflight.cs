using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Rules;

/// <summary>
/// T-25: a SystemRequired lookup has no legacy generator, so Dataverse rejects a create unless a
/// constant, one-of or lookupRandom rule supplies it. Needs only metadata and rules, so Bulk runs
/// it before the first write and Review runs it before Start (CR-003).
/// </summary>
public static class RequiredLookupPreflight
{
    /// <summary>One message per SystemRequired lookup on <paramref name="meta"/> that no rule supplies; empty when the table can be created.</summary>
    /// <param name="table">Entity logical name used in the messages.</param>
    /// <param name="meta">Live entity metadata.</param>
    /// <param name="rules">The table's validated rules, keyed case-insensitively; null when it has none.</param>
    public static IReadOnlyList<string> FindUnsupplied(
        string table, EntityMetadata meta, IReadOnlyDictionary<string, FieldRule>? rules)
    {
        ArgumentNullException.ThrowIfNull(meta);

        List<string> messages = [];
        foreach (var attr in (meta.Attributes ?? []).OfType<LookupAttributeMetadata>())
        {
            if (attr.LogicalName is null
                || attr.RequiredLevel?.Value != AttributeRequiredLevel.SystemRequired
                // ownerid is SystemRequired on every table; Dataverse defaults it to the calling user.
                || string.Equals(attr.LogicalName, "ownerid", StringComparison.OrdinalIgnoreCase)
                || (rules is not null && rules.TryGetValue(attr.LogicalName, out var rule)
                    && rule is ConstantRule or OneOfRule or LookupRandomRule))
                continue;

            messages.Add(
                $"Entity '{table}': required lookup '{attr.LogicalName}' (SystemRequired) has no generator — add a constant, one-of, or random-lookup rule for it.");
        }

        return messages;
    }
}

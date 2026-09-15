namespace DataGen.Core.Rules;

/// <summary>Maps graph/backfill column names to explicit lookup rule ownership.</summary>
public static class LookupRulePolicy
{
    /// <summary>Checks ownership without depending on caller dictionary comparers.</summary>
    /// <param name="rules">Configured rules; validation remains mandatory before writes.</param>
    /// <param name="table">Source table, never the target table.</param>
    /// <param name="column">Source lookup logical name.</param>
    public static bool IsExplicit(IReadOnlyDictionary<string, Dictionary<string, FieldRule>>? rules,
        string table, string column)
        => Matches(rules, table, column,
            static rule => rule is ConstantRule or OneOfRule or LookupRandomRule or NullRule);

    /// <summary>
    /// True when the rule supplies its own value with no dependency on the target table, so the
    /// column must not contribute a graph edge. <see cref="LookupRandomRule"/> is deliberately
    /// excluded: it may draw from records this run creates, which requires the target to sort first.
    /// </summary>
    /// <param name="rules">Configured rules; validation remains mandatory before writes.</param>
    /// <param name="table">Source table, never the target table.</param>
    /// <param name="column">Source lookup logical name.</param>
    public static bool SuppliesValueWithoutDependency(
        IReadOnlyDictionary<string, Dictionary<string, FieldRule>>? rules, string table, string column)
        => Matches(rules, table, column,
            static rule => rule is ConstantRule or OneOfRule or NullRule);

    private static bool Matches(IReadOnlyDictionary<string, Dictionary<string, FieldRule>>? rules,
        string table, string column, Func<FieldRule, bool> predicate)
    {
        if (rules is null) return false;
        foreach (var pair in rules)
            if (string.Equals(pair.Key, table, StringComparison.OrdinalIgnoreCase))
                foreach (var field in pair.Value)
                    if (string.Equals(field.Key, column, StringComparison.OrdinalIgnoreCase))
                        return predicate(field.Value);
        return false;
    }
}

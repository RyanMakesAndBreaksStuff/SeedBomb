namespace DataGen.Core.Contracts;

/// <summary>
/// A lightweight summary of a Dataverse entity for listing purposes.
/// </summary>
/// <param name="LogicalName">The entity logical name.</param>
/// <param name="DisplayName">The entity display name.</param>
/// <param name="IsCustom">
/// Whether this is a maker/ISV table (customization prefix that is not first-party).
/// First-party custom tables such as <c>msdyn_*</c> are not custom in this sense.
/// </param>
public record EntitySummary(string LogicalName, string DisplayName, bool IsCustom)
{
    /// <summary>
    /// True when Dataverse reports a custom entity whose logical name uses a
    /// non-Microsoft publisher prefix. User-created tables always have
    /// <c>{prefix}_{name}</c>. First-party tables (<c>msdyn_*</c>, <c>mspp_*</c>, …)
    /// also set <c>IsCustomEntity</c> and must not count as user-created.
    /// </summary>
    /// <param name="logicalName">Entity logical name.</param>
    /// <param name="isCustomEntity">Value of <c>EntityMetadata.IsCustomEntity</c>.</param>
    public static bool IsUserCreated(string? logicalName, bool isCustomEntity)
    {
        if (!isCustomEntity || string.IsNullOrEmpty(logicalName))
            return false;

        var sep = logicalName.IndexOf('_');
        if (sep <= 0)
            return false;

        var prefix = logicalName.AsSpan(0, sep);
        return !IsFirstPartyPrefix(prefix);
    }

    private static bool IsFirstPartyPrefix(ReadOnlySpan<char> prefix) =>
        prefix.StartsWith("msdyn", StringComparison.OrdinalIgnoreCase)
        || prefix.StartsWith("msfp", StringComparison.OrdinalIgnoreCase)
        || prefix.StartsWith("mspp", StringComparison.OrdinalIgnoreCase)
        || prefix.StartsWith("msfsi", StringComparison.OrdinalIgnoreCase)
        || prefix.StartsWith("powerpage", StringComparison.OrdinalIgnoreCase)
        || prefix.Equals("adx", StringComparison.OrdinalIgnoreCase)
        || prefix.Equals("mscrm", StringComparison.OrdinalIgnoreCase)
        || prefix.Equals("spstudio", StringComparison.OrdinalIgnoreCase);
}

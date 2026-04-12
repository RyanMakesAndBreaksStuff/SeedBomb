namespace DataGen.Core.Contracts;

/// <summary>
/// A lightweight summary of a Dataverse entity for listing purposes.
/// </summary>
/// <param name="LogicalName">The entity logical name.</param>
/// <param name="DisplayName">The entity display name.</param>
/// <param name="IsCustom">Whether the entity is a custom entity.</param>
public record EntitySummary(string LogicalName, string DisplayName, bool IsCustom);

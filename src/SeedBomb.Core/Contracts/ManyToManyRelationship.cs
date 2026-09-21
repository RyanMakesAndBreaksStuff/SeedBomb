namespace DataGen.Core.Contracts;

/// <summary>
/// Represents a many-to-many relationship between two Dataverse entities.
/// </summary>
/// <param name="SchemaName">The schema name of the relationship.</param>
/// <param name="Entity1LogicalName">The logical name of the first entity.</param>
/// <param name="Entity2LogicalName">The logical name of the second entity.</param>
public record ManyToManyRelationship(string SchemaName, string Entity1LogicalName, string Entity2LogicalName);

namespace DataGen.Core.Contracts;

/// <summary>
/// Represents a lookup field whose value must be backfilled after initial record creation
/// because it participates in a cycle that was broken by deferral.
/// </summary>
/// <param name="SourceEntity">The entity that contains the lookup field.</param>
/// <param name="FieldLogicalName">The logical name of the lookup field.</param>
/// <param name="TargetEntities">The possible target entities for this lookup.</param>
public record DeferredLookup(string SourceEntity, string FieldLogicalName, string[] TargetEntities);

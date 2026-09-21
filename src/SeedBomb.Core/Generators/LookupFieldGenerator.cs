using Bogus;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Generators;

/// <summary>
/// Generates fake lookup (entity reference) values using the record pool.
/// </summary>
internal sealed class LookupFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var lookupMeta = (LookupAttributeMetadata)metadata;
        var targets = lookupMeta.Targets;

        if (targets is null || targets.Length == 0)
            return null;

        // For polymorphic lookups, pick a random target entity
        var targetEntity = targets.Length == 1
            ? targets[0]
            : faker.PickRandom(targets);

        var id = pool.GetRandom(targetEntity, faker);
        return id.HasValue ? new EntityReference(targetEntity, id.Value) : null;
    }
}

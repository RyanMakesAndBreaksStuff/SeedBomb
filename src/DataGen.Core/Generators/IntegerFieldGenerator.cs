using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake integer values within the attribute's min/max range.
/// </summary>
internal sealed class IntegerFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is IntegerAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var intMeta = (IntegerAttributeMetadata)metadata;
        var min = intMeta.MinValue ?? 0;
        var max = intMeta.MaxValue ?? 100;
        return faker.Random.Int(min, max);
    }
}

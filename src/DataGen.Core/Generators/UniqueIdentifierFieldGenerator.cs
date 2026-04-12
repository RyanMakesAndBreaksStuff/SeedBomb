using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake unique identifier (GUID) values.
/// </summary>
internal sealed class UniqueIdentifierFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is UniqueIdentifierAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
        => faker.Random.Guid();
}

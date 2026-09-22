using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Generators;

/// <summary>
/// Generates fake unique identifier (GUID) values.
/// </summary>
internal sealed class UniqueIdentifierFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
        => faker.Random.Guid();
}

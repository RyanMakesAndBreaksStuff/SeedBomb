using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Generators;

/// <summary>
/// Generates fake boolean values.
/// </summary>
internal sealed class BooleanFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
        => faker.Random.Bool();
}

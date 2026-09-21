using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake values for a specific Dataverse attribute type.
/// </summary>
public interface IFieldGenerator
{
    /// <summary>
    /// Generates a fake value for the given attribute.
    /// </summary>
    /// <param name="metadata">The attribute metadata.</param>
    /// <param name="faker">The seeded Faker instance.</param>
    /// <param name="pool">The record pool for lookup resolution.</param>
    /// <returns>The generated value, or null if no value can be produced.</returns>
    object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool);
}

using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Generators;

/// <summary>
/// Generates fake double (floating-point) values within the attribute's min/max range.
/// </summary>
internal sealed class DoubleFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var dblMeta = (DoubleAttributeMetadata)metadata;
        var min = dblMeta.MinValue ?? 0.0;
        var max = dblMeta.MaxValue ?? 10000.0;
        var precision = dblMeta.Precision ?? 2;

        return Math.Round(faker.Random.Double(min, max), precision);
    }
}

using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake decimal values within the attribute's min/max range.
/// </summary>
internal sealed class DecimalFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var decMeta = (DecimalAttributeMetadata)metadata;
        var min = decMeta.MinValue ?? 0m;
        var max = decMeta.MaxValue ?? 10000m;
        var precision = decMeta.Precision ?? 2;
        return Math.Round(faker.Random.Decimal(min, max), precision);
    }
}

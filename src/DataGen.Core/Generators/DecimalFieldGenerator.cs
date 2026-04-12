using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake decimal values within the attribute's min/max range.
/// </summary>
internal sealed class DecimalFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is DecimalAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var decMeta = (DecimalAttributeMetadata)metadata;
        var min = (double)(decMeta.MinValue ?? 0m);
        var max = (double)(decMeta.MaxValue ?? 10000m);
        var precision = decMeta.Precision ?? 2;

        return Math.Round(Convert.ToDecimal(faker.Random.Double(min, max)), precision);
    }
}

using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake integer values within the attribute's min/max range,
/// with special handling for timezone, language, and duration formats.
/// </summary>
internal sealed class IntegerFieldGenerator : IFieldGenerator
{
    // Subset of valid Dataverse timezonedefinition.timezonecode values.
    private static readonly int[] ValidTimezoneCodes =
    [
        4, 10, 13, 15, 20, 25, 35, 55, 60, 70, 85,
        90, 95, 105, 110, 115, 130, 145, 165, 185,
        190, 205, 230, 250, 255
    ];

    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is IntegerAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var intMeta = (IntegerAttributeMetadata)metadata;

        return intMeta.Format switch
        {
            IntegerFormat.TimeZone => faker.PickRandom(ValidTimezoneCodes),
            // Duration is stored in minutes; keep it a reasonable positive span.
            IntegerFormat.Duration => faker.Random.Int(0, 480),
            // Some timezone fields report Format=null in metadata but Dataverse still validates against timezonedefinition.
            _ when intMeta.LogicalName?.EndsWith("utcoffset", StringComparison.OrdinalIgnoreCase) == true
                => faker.PickRandom(ValidTimezoneCodes),
            _ => faker.Random.Int(intMeta.MinValue ?? 0, intMeta.MaxValue ?? 100)
        };
    }
}

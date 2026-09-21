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

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var intMeta = (IntegerAttributeMetadata)metadata;

        return intMeta.Format switch
        {
            IntegerFormat.TimeZone => faker.PickRandom(ValidTimezoneCodes),
            // Duration is stored in minutes; keep it a reasonable positive span.
            IntegerFormat.Duration => faker.Random.Int(0, 480),
            // Some timezone fields report Format=null in metadata but Dataverse still validates
            // against timezonedefinition (e.g. utcconversiontimezonecode on every entity,
            // address1_utcoffset, etc.).
            _ when IsTimeZoneCodeAttribute(intMeta.LogicalName)
                => faker.PickRandom(ValidTimezoneCodes),
            // timezoneruleversionnumber is an opaque version stamp — Dataverse expects it to be
            // either a known version or -1; safest is a small non-negative integer.
            _ when intMeta.LogicalName?.Equals("timezoneruleversionnumber", StringComparison.OrdinalIgnoreCase) == true
                => 0,
            _ => faker.Random.Int(intMeta.MinValue ?? 0, intMeta.MaxValue ?? 100)
        };
    }

    private static bool IsTimeZoneCodeAttribute(string? logicalName)
    {
        if (string.IsNullOrEmpty(logicalName)) return false;
        return logicalName.EndsWith("utcoffset", StringComparison.OrdinalIgnoreCase)
            || logicalName.EndsWith("timezonecode", StringComparison.OrdinalIgnoreCase);
    }
}

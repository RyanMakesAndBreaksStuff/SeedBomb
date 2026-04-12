using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake date-time values respecting date-only vs date-time behavior.
/// </summary>
internal sealed class DateTimeFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is DateTimeAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var dtMeta = (DateTimeAttributeMetadata)metadata;
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = DeterministicFaker.ReferenceDate;

        var date = faker.Date.Between(start, end);

        // DateOnly behavior strips time component
        if (dtMeta.DateTimeBehavior?.Value == "DateOnly")
            return date.Date;

        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }
}

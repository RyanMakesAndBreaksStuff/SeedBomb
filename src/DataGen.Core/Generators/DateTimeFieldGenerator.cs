using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake date-time values respecting date-only vs date-time behavior.
/// </summary>
internal sealed class DateTimeFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var dtMeta = (DateTimeAttributeMetadata)metadata;
        var start = DateTimeAttributeMetadata.MinSupportedValue > DateTime.MinValue
            ? DateTimeAttributeMetadata.MinSupportedValue
            : new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = DateTime.UtcNow.AddYears(15);

        var date = faker.Date.Between(start, end);

        // DateOnly behavior strips time component
        if (dtMeta.DateTimeBehavior?.Value == "DateOnly")
            return date.Date;
        if (dtMeta.DateTimeBehavior?.Value == "TimeZoneIndependent")
            return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);

        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }
}

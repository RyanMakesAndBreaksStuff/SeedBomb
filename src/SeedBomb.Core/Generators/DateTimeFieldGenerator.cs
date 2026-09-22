using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Generators;

/// <summary>
/// Generates fake date-time values respecting date-only vs date-time behavior.
/// </summary>
internal sealed class DateTimeFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var dtMeta = (DateTimeAttributeMetadata)metadata;
        var (start, end) = GetDateRange(dtMeta.LogicalName ?? string.Empty);

        var date = faker.Date.Between(start, end);

        if (dtMeta.DateTimeBehavior?.Value == "DateOnly")
            return date.Date;
        if (dtMeta.DateTimeBehavior?.Value == "TimeZoneIndependent")
            return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);

        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }

    private static (DateTime Start, DateTime End) GetDateRange(string logicalName)
    {
        var lower = logicalName.ToLowerInvariant();
        if (lower is "createdon" or "overriddencreatedon" or "modifiedon")
            return (DateTime.UtcNow.AddYears(-10), DateTime.UtcNow);
        if (lower is "birthdate" or "anniversary")
            return (DateTime.UtcNow.AddYears(-70), DateTime.UtcNow.AddYears(-18));

        var defaultStart = DateTimeAttributeMetadata.MinSupportedValue > DateTime.MinValue
            ? DateTimeAttributeMetadata.MinSupportedValue
            : new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (defaultStart, DateTime.UtcNow.AddYears(15));
    }
}

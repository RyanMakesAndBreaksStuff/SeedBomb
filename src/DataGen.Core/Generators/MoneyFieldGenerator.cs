using Bogus;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake money values respecting precision constraints.
/// </summary>
internal sealed class MoneyFieldGenerator : IFieldGenerator
{
    private const decimal MaxReasonableValue = 10_000_000m;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var moneyMeta = (MoneyAttributeMetadata)metadata;
        var min = (decimal)(moneyMeta.MinValue ?? 0.0);
        var max = Math.Min((decimal)(moneyMeta.MaxValue ?? (double)MaxReasonableValue), MaxReasonableValue);
        if (min > max) max = min;
        var precision = moneyMeta.Precision ?? 2;
        return new Money(Math.Round(faker.Random.Decimal(min, max), precision));
    }
}

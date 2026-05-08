using Bogus;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake money values respecting precision constraints.
/// </summary>
internal sealed class MoneyFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is MoneyAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var moneyMeta = (MoneyAttributeMetadata)metadata;
        var min = (decimal)(moneyMeta.MinValue ?? 0.0);
        var max = (decimal)(moneyMeta.MaxValue ?? 10000.0);
        var precision = moneyMeta.Precision ?? 2;
        return new Money(Math.Round(faker.Random.Decimal(min, max), precision));
    }
}

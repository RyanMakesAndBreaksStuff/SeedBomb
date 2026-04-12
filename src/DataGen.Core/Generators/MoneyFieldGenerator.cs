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
        var min = moneyMeta.MinValue ?? 0.0;
        var max = moneyMeta.MaxValue ?? 10000.0;
        var precision = moneyMeta.Precision ?? 2;

        var value = Math.Round(faker.Random.Double(min, max), precision);
        return new Money(Convert.ToDecimal(value));
    }
}

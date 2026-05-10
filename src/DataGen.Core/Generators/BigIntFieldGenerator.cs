using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

internal sealed class BigIntFieldGenerator : IFieldGenerator
{
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is BigIntAttributeMetadata;

    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var bigIntMeta = (BigIntAttributeMetadata)metadata;
        var min = bigIntMeta.MinValue ?? 0L;
        var max = bigIntMeta.MaxValue ?? 1_000_000_000L;
        return faker.Random.Long(min, max);
    }
}

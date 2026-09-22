using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Generators;

internal sealed class BigIntFieldGenerator : IFieldGenerator
{

    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var bigIntMeta = (BigIntAttributeMetadata)metadata;
        var min = bigIntMeta.MinValue ?? 0L;
        var max = bigIntMeta.MaxValue ?? 1_000_000_000L;
        return faker.Random.Long(min, max);
    }
}

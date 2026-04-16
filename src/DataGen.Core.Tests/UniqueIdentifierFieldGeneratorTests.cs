using Bogus;

namespace DataGen.Core.Tests;

public class UniqueIdentifierFieldGeneratorTests
{
    private readonly UniqueIdentifierFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void CanGenerate_UniqueIdentifierMetadata_ReturnsTrue()
        => Assert.True(_gen.CanGenerate(new UniqueIdentifierAttributeMetadata()));

    [Fact]
    public void CanGenerate_StringMetadata_ReturnsFalse()
        => Assert.False(_gen.CanGenerate(new StringAttributeMetadata()));

    [Fact]
    public void Generate_ReturnsNonEmptyGuid()
    {
        var attr = new UniqueIdentifierAttributeMetadata { LogicalName = "entityid" };
        var result = (Guid)_gen.Generate(attr, _faker, _pool)!;
        Assert.NotEqual(Guid.Empty, result);
    }
}

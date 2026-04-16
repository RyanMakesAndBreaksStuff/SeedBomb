using Bogus;

namespace DataGen.Core.Tests;

public class DecimalFieldGeneratorTests
{
    private readonly DecimalFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void CanGenerate_DecimalMetadata_ReturnsTrue()
        => Assert.True(_gen.CanGenerate(new DecimalAttributeMetadata()));

    [Fact]
    public void CanGenerate_StringMetadata_ReturnsFalse()
        => Assert.False(_gen.CanGenerate(new StringAttributeMetadata()));

    [Fact]
    public void Generate_ReturnsDecimal()
    {
        var attr = new DecimalAttributeMetadata { LogicalName = "weight", MinValue = 0m, MaxValue = 500m, Precision = 2 };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<decimal>(result);
    }

    [Fact]
    public void Generate_RespectsRange()
    {
        var attr = new DecimalAttributeMetadata { LogicalName = "weight", MinValue = 1m, MaxValue = 10m };
        var result = (decimal)_gen.Generate(attr, _faker, _pool)!;
        Assert.True(result >= 1m && result <= 10m, $"Value {result} is outside [1, 10]");
    }
}

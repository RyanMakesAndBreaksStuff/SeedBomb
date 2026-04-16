using Bogus;

namespace DataGen.Core.Tests;

public class MoneyFieldGeneratorTests
{
    private readonly MoneyFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void CanGenerate_MoneyMetadata_ReturnsTrue()
        => Assert.True(_gen.CanGenerate(new MoneyAttributeMetadata()));

    [Fact]
    public void CanGenerate_StringMetadata_ReturnsFalse()
        => Assert.False(_gen.CanGenerate(new StringAttributeMetadata()));

    [Fact]
    public void Generate_ReturnsMoneyObject()
    {
        var attr = new MoneyAttributeMetadata { LogicalName = "revenue" };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<Money>(result);
    }

    [Fact]
    public void Generate_ValueWithinDefaultRange()
    {
        var attr = new MoneyAttributeMetadata { LogicalName = "revenue", MinValue = 0.0, MaxValue = 1000.0 };
        var result = (Money)_gen.Generate(attr, _faker, _pool)!;
        Assert.True(result.Value >= 0m && result.Value <= 1000m,
            $"Value {result.Value} is outside [0, 1000]");
    }

    [Fact]
    public void Generate_NullMinMax_UsesDefaults()
    {
        var attr = new MoneyAttributeMetadata { LogicalName = "amount", MinValue = null, MaxValue = null };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<Money>(result);
    }
}

using Bogus;

namespace DataGen.Core.Tests;

public class IntegerFieldGeneratorTests
{
    private readonly IntegerFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void CanGenerate_IntegerMetadata_ReturnsTrue()
        => Assert.True(_gen.CanGenerate(new IntegerAttributeMetadata()));

    [Fact]
    public void CanGenerate_StringMetadata_ReturnsFalse()
        => Assert.False(_gen.CanGenerate(new StringAttributeMetadata()));

    [Fact]
    public void Generate_ReturnsInt()
    {
        var attr = new IntegerAttributeMetadata { LogicalName = "count", MinValue = 0, MaxValue = 100 };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<int>(result);
    }

    [Fact]
    public void Generate_RespectsMinMax()
    {
        var attr = new IntegerAttributeMetadata { LogicalName = "priority", MinValue = 5, MaxValue = 10 };
        var result = (int)_gen.Generate(attr, _faker, _pool)!;
        Assert.True(result >= 5 && result <= 10, $"Value {result} is outside [5, 10]");
    }

    [Fact]
    public void Generate_NullMinMax_UsesDefaults()
    {
        var attr = new IntegerAttributeMetadata { LogicalName = "count", MinValue = null, MaxValue = null };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<int>(result);
    }
}

using Bogus;

namespace DataGen.Core.Tests;

public class DoubleFieldGeneratorTests
{
    private readonly DoubleFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);


    [Fact]
    public void Generate_ReturnsDouble()
    {
        var attr = new DoubleAttributeMetadata { LogicalName = "latitude", MinValue = -90.0, MaxValue = 90.0, Precision = 5 };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<double>(result);
    }

    [Fact]
    public void Generate_RespectsRange()
    {
        var attr = new DoubleAttributeMetadata { LogicalName = "rating", MinValue = 0.0, MaxValue = 5.0 };
        var result = (double)_gen.Generate(attr, _faker, _pool)!;
        Assert.True(result >= 0.0 && result <= 5.0, $"Value {result} is outside [0, 5]");
    }
}

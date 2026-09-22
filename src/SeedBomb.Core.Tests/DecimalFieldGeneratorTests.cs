using Bogus;

namespace SeedBomb.Core.Tests;

public class DecimalFieldGeneratorTests
{
    private readonly DecimalFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);


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

    [Fact]
    public void Generate_NoPrecisionLossViaDoubleIntermediate()
    {
        var attr = new DecimalAttributeMetadata
        {
            LogicalName = "rate",
            MinValue = 0.001m,
            MaxValue = 0.999m,
            Precision = 3
        };
        var result = (decimal)_gen.Generate(attr, _faker, _pool)!;
        Assert.True(result >= 0.001m && result <= 0.999m,
            $"Value {result} outside [0.001, 0.999]");
        // Verify scale does not exceed declared precision.
        var scale = (decimal.GetBits(result)[3] >> 16) & 0xFF;
        Assert.True(scale <= 3, $"Decimal scale {scale} exceeds precision 3");
    }
}

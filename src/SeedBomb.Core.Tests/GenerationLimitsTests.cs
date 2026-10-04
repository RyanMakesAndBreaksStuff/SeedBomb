namespace SeedBomb.Core.Tests;

public class GenerationLimitsTests
{
    private static GenerationConfig Config(int batch, int? dop, int count) => new()
    {
        EntityLogicalNames = ["account"],
        RecordCounts = new Dictionary<string, int> { ["account"] = count },
        BatchSize = batch,
        MaxParallelism = dop,
    };

    [Theory]
    [InlineData(0, null, 10)]
    [InlineData(1001, null, 10)]
    [InlineData(500, 0, 10)]
    [InlineData(500, 17, 10)]
    [InlineData(500, null, 100_001)]
    public void Validate_rejects_values_outside_the_bounds(int batch, int? dop, int count) =>
        Assert.Throws<DataGenerationException>(() => GenerationLimits.Validate(Config(batch, dop, count)));

    [Fact]
    public void Validate_accepts_the_bounds_themselves()
    {
        GenerationLimits.Validate(Config(1, 1, 1));
        GenerationLimits.Validate(Config(
            GenerationLimits.MaxBatchSize, GenerationLimits.MaxDop, GenerationLimits.MaxRecordCount));
    }
}

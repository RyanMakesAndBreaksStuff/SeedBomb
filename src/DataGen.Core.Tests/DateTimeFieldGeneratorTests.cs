using Bogus;

namespace DataGen.Core.Tests;

public class DateTimeFieldGeneratorTests
{
    private readonly DateTimeFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void CanGenerate_DateTimeMetadata_ReturnsTrue()
        => Assert.True(_gen.CanGenerate(new DateTimeAttributeMetadata()));

    [Fact]
    public void CanGenerate_StringMetadata_ReturnsFalse()
        => Assert.False(_gen.CanGenerate(new StringAttributeMetadata()));

    [Fact]
    public void Generate_DateOnlyBehavior_ReturnsDateWithNoTime()
    {
        var attr = new DateTimeAttributeMetadata
        {
            LogicalName = "birthdate",
            DateTimeBehavior = DateTimeBehavior.DateOnly
        };
        var result = (DateTime)_gen.Generate(attr, _faker, _pool)!;

        Assert.Equal(TimeSpan.Zero, result.TimeOfDay);
    }

    [Fact]
    public void Generate_UserLocalBehavior_ReturnsUtcDateTime()
    {
        var attr = new DateTimeAttributeMetadata
        {
            LogicalName = "createdon",
            DateTimeBehavior = DateTimeBehavior.UserLocal
        };
        var result = (DateTime)_gen.Generate(attr, _faker, _pool)!;

        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Generate_ValueInExpectedRange()
    {
        var attr = new DateTimeAttributeMetadata { LogicalName = "createdon" };
        var result = (DateTime)_gen.Generate(attr, _faker, _pool)!;

        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(result >= start && result <= DeterministicFaker.ReferenceDate,
            $"Date {result} is outside expected range");
    }
}

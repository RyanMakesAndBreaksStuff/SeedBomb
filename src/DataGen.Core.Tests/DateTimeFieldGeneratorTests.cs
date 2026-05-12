using Bogus;

namespace DataGen.Core.Tests;

public class DateTimeFieldGeneratorTests
{
    private readonly DateTimeFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);


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
    public void Generate_TimeZoneIndependent_ReturnsUnspecifiedKind()
    {
        var attr = new DateTimeAttributeMetadata
        {
            LogicalName = "scheduledstart",
            DateTimeBehavior = DateTimeBehavior.TimeZoneIndependent
        };
        var result = (DateTime)_gen.Generate(attr, _faker, _pool)!;
        Assert.Equal(DateTimeKind.Unspecified, result.Kind);
    }

    [Fact]
    public void Generate_ValueInExpectedRange()
    {
        var attr = new DateTimeAttributeMetadata { LogicalName = "createdon" };
        var result = (DateTime)_gen.Generate(attr, _faker, _pool)!;

        var start = DateTimeAttributeMetadata.MinSupportedValue > DateTime.MinValue
            ? DateTimeAttributeMetadata.MinSupportedValue
            : new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = DateTime.UtcNow.AddYears(15);
        Assert.True(result >= start && result <= end,
            $"Date {result} is outside expected range [{start}, {end}]");
    }
}

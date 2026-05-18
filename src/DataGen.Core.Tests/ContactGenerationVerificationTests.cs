using Bogus;

namespace DataGen.Core.Tests;

public class ContactGenerationVerificationTests
{
    private readonly GeneratorFactory _factory = new(NullLogger<GeneratorFactory>.Instance);
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void ContactFields_FieldFilter_AllowsNamesAndPhones()
    {
        var fields = new AttributeMetadata[]
        {
            new StringAttributeMetadata { LogicalName = "firstname", IsValidForCreate = true },
            new StringAttributeMetadata { LogicalName = "lastname", IsValidForCreate = true, RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired) },
            new StringAttributeMetadata { LogicalName = "telephone1", IsValidForCreate = null },
            new StringAttributeMetadata { LogicalName = "mobilephone", IsValidForCreate = null },
        };

        foreach (var field in fields)
        {
            Assert.True(FieldFilter.ShouldGenerateField(field), $"{field.LogicalName} should be generated");
        }
    }

    [Fact]
    public void ContactFields_CreatedOn_IsWithinLastTenYears()
    {
        var attr = new DateTimeAttributeMetadata
        {
            LogicalName = "createdon",
            DateTimeBehavior = DateTimeBehavior.UserLocal
        };
        var result = (DateTime)_factory.Generate(attr, _faker, _pool)!;

        var now = DateTime.UtcNow;
        var floor = now.AddYears(-10);
        Assert.True(result >= floor && result <= now,
            $"createdon {result:o} outside [{floor:o}, {now:o}]");
    }

    [Fact]
    public void ContactFields_BirthDate_IsBetween18And70YearsAgo()
    {
        var attr = new DateTimeAttributeMetadata
        {
            LogicalName = "birthdate",
            DateTimeBehavior = DateTimeBehavior.DateOnly
        };
        var result = (DateTime)_factory.Generate(attr, _faker, _pool)!;

        var now = DateTime.UtcNow;
        var floor = now.AddYears(-70);
        var ceiling = now.AddYears(-18);
        Assert.True(result >= floor && result <= ceiling,
            $"birthdate {result:o} outside [{floor:o}, {ceiling:o}]");
    }

    [Fact]
    public void ContactFields_CreditLimit_IsAtMostTenMillion()
    {
        var attr = new MoneyAttributeMetadata
        {
            LogicalName = "creditlimit",
            MinValue = 0.0,
            MaxValue = 100_000_000_000_000.0,
            Precision = 2
        };
        var result = (Money)_factory.Generate(attr, _faker, _pool)!;

        Assert.True(result.Value <= 10_000_000m,
            $"creditlimit {result.Value} exceeds 10M cap");
        Assert.True(result.Value >= 0m,
            $"creditlimit {result.Value} below 0");
    }
}

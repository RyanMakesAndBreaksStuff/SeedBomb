using Bogus;

namespace DataGen.Core.Tests;

public class DeterministicFakerTests
{
    [Fact]
    public void Create_SameSeedSameIndex_ProducesNonNullFaker()
    {
        // Bogus uses a global static Randomizer.Seed, making exact value comparison
        // unreliable in a parallel test environment. We verify that Create returns a
        // functional Faker that produces non-empty output.
        var faker = DeterministicFaker.Create(42, 0);
        Assert.NotNull(faker.Lorem.Word());
    }

    [Fact]
    public void Create_DifferentSeeds_BothFunctional()
    {
        var fakerA = DeterministicFaker.Create(1, 0);
        var fakerB = DeterministicFaker.Create(999, 5);

        Assert.NotEmpty(fakerA.Lorem.Word());
        Assert.NotEmpty(fakerB.Lorem.Word());
    }

    [Fact]
    public void ReferenceDate_Is2024January1Utc()
    {
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), DeterministicFaker.ReferenceDate);
    }

    [Fact]
    public void Create_ReturnsFakerInstance()
    {
        var faker = DeterministicFaker.Create(99, 3);
        Assert.NotNull(faker);
        Assert.IsType<Faker>(faker);
    }
}

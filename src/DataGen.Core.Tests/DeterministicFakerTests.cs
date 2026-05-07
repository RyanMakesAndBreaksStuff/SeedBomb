using Bogus;

namespace DataGen.Core.Tests;

public class DeterministicFakerTests
{
    [Fact]
    public void Create_SameSeedSameIndex_ProducesSameSequence()
    {
        // Per-instance seed: same inputs must produce identical first values.
        var f1 = DeterministicFaker.Create(42, 0);
        var f2 = DeterministicFaker.Create(42, 0);
        Assert.Equal(f1.Random.Int(0, 10_000), f2.Random.Int(0, 10_000));
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

    [Fact]
    public void Create_DifferentEntityIndex_ProducesDifferentSequence()
    {
        var f0 = DeterministicFaker.Create(42, 0);
        var f1 = DeterministicFaker.Create(42, 1);
        // Different entity index → different seed → different first value (overwhelmingly probable).
        Assert.NotEqual(f0.Random.Int(0, int.MaxValue), f1.Random.Int(0, int.MaxValue));
    }

    [Fact]
    public void Create_DoesNotMutateGlobalRandomizerSeed()
    {
        var sentinel = new Random(12345);
        Randomizer.Seed = sentinel;

        DeterministicFaker.Create(42, 0);
        DeterministicFaker.Create(42, 1);

        // Randomizer.Seed must be the exact same object reference — not replaced.
        Assert.Same(sentinel, Randomizer.Seed);
    }
}

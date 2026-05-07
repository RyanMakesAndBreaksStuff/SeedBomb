using Bogus;

namespace DataGen.Core.Generators;

/// <summary>
/// Creates deterministic Faker instances with a fixed seed and reference date
/// so that data generation is reproducible across runs.
/// </summary>
public static class DeterministicFaker
{
    /// <summary>
    /// The fixed reference date used for all date-time generation.
    /// </summary>
    public static readonly DateTime ReferenceDate = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Creates a deterministic Faker instance for the given entity.
    /// </summary>
    /// <param name="baseSeed">The base seed from the generation configuration.</param>
    /// <param name="entityIndex">The index of the entity in topological sort order.</param>
    /// <returns>A seeded Faker instance.</returns>
    public static Faker Create(int baseSeed, int entityIndex)
    {
        return new Faker { Random = new Randomizer(baseSeed + entityIndex) };
    }
}

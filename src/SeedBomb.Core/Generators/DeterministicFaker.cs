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

    /// <summary>Run-level Bogus locale until a picker ships. Matches <c>GenerationConfig.Locale</c>.</summary>
    public const string DefaultLocale = "en";

    /// <summary>
    /// Creates a deterministic Faker instance for the given entity.
    /// </summary>
    /// <param name="baseSeed">The base seed from the generation configuration.</param>
    /// <param name="entityIndex">The index of the entity in topological sort order.</param>
    /// <param name="locale">Bogus locale. Blank or null falls back to <see cref="DefaultLocale"/>.</param>
    /// <returns>A seeded Faker instance.</returns>
    public static Faker Create(int baseSeed, int entityIndex, string? locale = null)
    {
        // Knuth multiplicative hash (32-bit). Stable across runtimes/machines.
        // Collision space: 2^32; >65k entity-seed pairs increases collision probability.
        var mixed = unchecked((int)((uint)baseSeed * 2654435761u) ^ entityIndex);
        var resolved = string.IsNullOrWhiteSpace(locale) ? DefaultLocale : locale;
        return new Faker(resolved) { Random = new Randomizer(mixed) };
    }
}

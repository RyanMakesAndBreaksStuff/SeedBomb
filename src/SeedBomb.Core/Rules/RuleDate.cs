using System.Globalization;

namespace SeedBomb.Core.Rules;

/// <summary>
/// Single owner of how rule date text is read: invariant culture, and text without an offset is
/// UTC. A profile then yields the same dates on every machine, whatever its culture or time zone.
/// </summary>
internal static class RuleDate
{
    private const DateTimeStyles Utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>Parses <paramref name="text"/> to a <see cref="DateTimeKind.Utc"/> value.</summary>
    public static DateTime Parse(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, Utc);

    /// <summary>Tries to parse <paramref name="text"/> to a <see cref="DateTimeKind.Utc"/> value.</summary>
    public static bool TryParse(string? text, out DateTime value) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, Utc, out value);
}

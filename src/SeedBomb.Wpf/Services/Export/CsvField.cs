namespace SeedBomb.Services.Export;

/// <summary>
/// Formula-safe CSV field escaping shared by every CSV export in the app (run history, rejected
/// rows). A field is quoted when it contains a comma, quote, or newline. A field that begins with
/// a spreadsheet formula trigger (<c>= + - @</c>, or a leading tab/CR) is also prefixed with a
/// literal single quote, so a hostile value — e.g. an imported profile named
/// <c>=HYPERLINK(...)</c> — never executes as a formula when the CSV is opened in a spreadsheet.
/// </summary>
public static class CsvField
{
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>Escapes <paramref name="value"/> for one CSV field.</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var safe = value.Length > 0 && FormulaTriggers.Contains(value[0])
            ? "'" + value
            : value;

        if (safe.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return safe;

        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }
}
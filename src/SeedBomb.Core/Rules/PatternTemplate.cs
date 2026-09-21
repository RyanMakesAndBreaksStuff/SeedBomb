using System.Text;
using System.Text.RegularExpressions;

namespace SeedBomb.Core.Rules;

/// <summary>
/// Parsed pattern template: literals plus the closed token set {seq[:0N]}, {random:N}, {runId}.
/// Parsed, never evaluated (spec S4). {seq} is 1-based (row 0 → 1) to match the mocks (ACME-0001).
/// </summary>
public sealed partial class PatternTemplate
{
    private abstract record Segment;
    private sealed record Literal(string Text) : Segment;
    private sealed record Seq(int Pad) : Segment;          // Pad 0 = unpadded
    private sealed record Random(int Length) : Segment;
    private sealed record RunId : Segment;

    private readonly IReadOnlyList<Segment> _segments;
    private PatternTemplate(IReadOnlyList<Segment> segments) => _segments = segments;

    [GeneratedRegex(@"\{(seq(?::(0+))?|random:(\d{1,3})|runId)\}")]
    private static partial Regex TokenRegex();

    /// <summary>Parses a template; any brace content outside the closed grammar throws <see cref="FormatException"/>.</summary>
    public static PatternTemplate Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var segments = new List<Segment>();
        int pos = 0;
        foreach (Match m in TokenRegex().Matches(template))
        {
            AddLiteralChecked(segments, template[pos..m.Index]);
            segments.Add(m.Groups[1].Value switch
            {
                var s when s.StartsWith("seq") => new Seq(m.Groups[2].Value.Length),
                var s when s.StartsWith("random") => new Random(int.Parse(m.Groups[3].Value)),
                _ => new RunId(),
            });
            pos = m.Index + m.Length;
        }
        AddLiteralChecked(segments, template[pos..]);
        return new PatternTemplate(segments);
    }

    // Any stray brace left in a literal means an unknown/malformed token — hard error, never ignored.
    private static void AddLiteralChecked(List<Segment> segments, string text)
    {
        if (text.Contains('{') || text.Contains('}'))
            throw new FormatException($"Unknown or malformed template token near \"{text}\". Allowed: {{seq}}, {{seq:0N}}, {{random:N}}, {{runId}}.");
        if (text.Length > 0) segments.Add(new Literal(text));
    }

    /// <summary>Expands for one row. <paramref name="random"/> supplies N deterministic chars (see RuleValueGenerator).</summary>
    public string Expand(int rowIndex, Func<int, string> random, string runId)
    {
        var sb = new StringBuilder();
        foreach (var s in _segments)
            sb.Append(s switch
            {
                Literal l => l.Text,
                Seq q => (rowIndex + 1).ToString(q.Pad > 0 ? new string('0', q.Pad) : "D"),
                Random r => random(r.Length),
                RunId => runId,
                _ => throw new InvalidOperationException(),
            });
        return sb.ToString();
    }

    /// <summary>Worst-case expanded length for MaxLength validation (§3.3: reject, never truncate).</summary>
    public int MaxExpandedLength(int recordCount, int runIdLength)
        => _segments.Sum(s => s switch
        {
            Literal l => l.Text.Length,
            Seq q => Math.Max(q.Pad, recordCount.ToString().Length),
            Random r => r.Length,
            RunId => runIdLength,
            _ => 0,
        });

    /// <summary>Literal text with tokens removed — input to the reserved-domain warning check (D3).</summary>
    public string LiteralText() => string.Concat(_segments.OfType<Literal>().Select(l => l.Text));
}

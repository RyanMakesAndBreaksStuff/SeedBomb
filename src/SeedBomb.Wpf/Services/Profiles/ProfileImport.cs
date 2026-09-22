using SeedBomb.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Services.Profiles;

/// <summary>
/// Outcome of §07 metadata-stage import (layer 2). Board receives only
/// <see cref="BoardRules"/> — each value is an <c>EffectiveRule</c> after clamp.
/// Schema-stage failures never produce a report (Task 10 returns the single-line error instead).
/// </summary>
/// <param name="BoardRules">Applied rules keyed table → column → effective rule.</param>
/// <param name="TableCounts">Per-table counts adopted from the profile.</param>
/// <param name="Seed">Optional pinned seed from the profile.</param>
/// <param name="AppliedRuleCount">Rules that landed on the board.</param>
/// <param name="AppliedTableSummaries">e.g. <c>account (500)</c> for the success InfoBar.</param>
/// <param name="Adjusted">Clamp / warning lines (warning InfoBar).</param>
/// <param name="NotImported">Rejected rules with reasons (error InfoBar).</param>
/// <param name="ProfileName">Name of the imported profile, for the board's Profile card.</param>
public sealed record ProfileImportReport(
    IReadOnlyDictionary<string, Dictionary<string, FieldRule>> BoardRules,
    IReadOnlyDictionary<string, int> TableCounts,
    int? Seed,
    int AppliedRuleCount,
    IReadOnlyList<string> AppliedTableSummaries,
    IReadOnlyList<string> Adjusted,
    IReadOnlyList<string> NotImported,
    string ProfileName = "");

/// <summary>
/// Layer-2 import: runs <see cref="RuleValidator"/> against live metadata and partitions
/// outcomes into applied / adjusted-with-clamp / not-imported (Mock F5). Pure — no I/O.
/// </summary>
public static class ProfileImport
{
    /// <summary>
    /// Validates every authored rule in <paramref name="profile"/> against
    /// <paramref name="metadata"/>. Unknown tables/columns and hard validation errors go to
    /// not-imported; range clamps land in adjusted and the board receives the effective rule.
    /// </summary>
    public static ProfileImportReport ValidateAgainstMetadata(
        Profile profile,
        IReadOnlyDictionary<string, EntityMetadata> metadata,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(runId);

        var board = new Dictionary<string, Dictionary<string, FieldRule>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var appliedTables = new List<string>();
        var adjusted = new List<string>();
        var notImported = new List<string>();
        var appliedRules = 0;

        foreach (var table in profile.Tables)
        {
            counts[table.Table] = table.Count;

            if (!metadata.TryGetValue(table.Table, out var meta))
            {
                if (table.Columns is { Count: > 0 })
                {
                    foreach (var col in table.Columns.Keys)
                        notImported.Add($"{table.Table}.{col} — table not available in this environment.");
                }
                else
                {
                    notImported.Add($"{table.Table} — table not available in this environment.");
                }

                continue;
            }

            appliedTables.Add($"{table.Table} ({table.Count})");

            if (table.Columns is null || table.Columns.Count == 0)
                continue;

            var attrs = (meta.Attributes ?? [])
                .Where(a => a.LogicalName is not null)
                .ToDictionary(a => a.LogicalName!, StringComparer.OrdinalIgnoreCase);

            var tableBoard = new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase);

            foreach (var (column, rule) in table.Columns)
            {
                if (!attrs.TryGetValue(column, out var attr))
                {
                    notImported.Add($"{table.Table}.{column} — unknown column.");
                    continue;
                }

                var eligibility = RuleEligibility.Classify(attr);
                if (!eligibility.IsSettable)
                {
                    notImported.Add(
                        $"{table.Table}.{column} — platform-owned ({ToReasonCode(eligibility.Reason)}); rules can never target it.");
                    continue;
                }

                var result = RuleValidator.Validate(
                    rule, attr, new RuleValidationContext(table.Table, table.Count, runId));
                if (!result.IsValid || result.EffectiveRule is null)
                {
                    var detail = result.Messages.FirstOrDefault(m => m.Severity == RuleMessageSeverity.Error)?.Text
                                 ?? "rule rejected.";
                    notImported.Add($"{table.Table}.{column} — {detail}");
                    continue;
                }

                foreach (var warning in result.Messages.Where(m => m.Severity == RuleMessageSeverity.Warning))
                    adjusted.Add(warning.Text);

                tableBoard[column] = result.EffectiveRule;
                appliedRules++;
            }

            if (tableBoard.Count > 0)
                board[table.Table] = tableBoard;
        }

        return new ProfileImportReport(
            board,
            counts,
            profile.Seed,
            appliedRules,
            appliedTables,
            adjusted,
            notImported,
            profile.Name);
    }

    /// <summary>Spec §3.2 reason codes as SCREAMING_SNAKE for import reports (Mock F5).</summary>
    public static string ToReasonCode(EligibilityReason reason)
    {
        var name = reason.ToString();
        // BaseCurrency → BASE_CURRENCY
        var chars = new List<char>(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c))
                chars.Add('_');
            chars.Add(char.ToUpperInvariant(c));
        }

        return new string([.. chars]);
    }
}
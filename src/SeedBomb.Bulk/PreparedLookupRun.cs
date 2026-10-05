using SeedBomb.Core.Contracts;
using SeedBomb.Core.Exceptions;
using SeedBomb.Core.Generators;
using SeedBomb.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System.Collections.Frozen;

namespace SeedBomb.Bulk;

/// <summary>One column's candidate sources: rows read before the run, plus targets this run creates first.</summary>
/// <param name="Existing">Canonically ordered rows read from the environment before generation.</param>
/// <param name="InRunTargets">Lower-cased targets created earlier in topological order.</param>
/// <param name="PreferInRun">Implicit required-lookup rule: link to this run's parents, falling back to Existing (WR-002).</param>
internal sealed record PreparedLookupColumn(
    IReadOnlyList<LookupRuleValue> Existing, IReadOnlyList<string> InRunTargets, bool PreferInRun = false);

internal sealed class PreparedLookupRun(
    FrozenDictionary<string, PreparedLookupColumn> columns)
{
    internal const int PageSize = 500;
    internal const int MaximumCandidatesPerTarget = LookupRandomRule.MaximumCandidatesPerTarget;

    // Row generation for one table is sequential (BulkCreator generates before it submits), so the
    // first Get for a column resolves the pool once and every later row reuses the same list.
    private readonly Dictionary<string, IReadOnlyList<LookupRuleValue>> _resolved =
        new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<LookupRuleValue> Get(string table, string column, DataverseRecordPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var key = $"{table}.{column}";
        if (!columns.TryGetValue(key, out var prepared))
            throw new InvalidOperationException($"Lookup candidates were not prepared for '{key}'.");
        if (prepared.Existing.Count > 0 && !prepared.PreferInRun)
            return prepared.Existing;
        if (_resolved.TryGetValue(key, out var cached))
            return cached;

        var fromRun = prepared.InRunTargets
            .SelectMany(target => pool.Get(target).Select(id => new LookupRuleValue(target, id)))
            .OrderBy(v => v.Entity, StringComparer.Ordinal)
            .ThenBy(v => v.Id.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        if (fromRun.Length == 0 && prepared.Existing.Count > 0)
            return prepared.Existing;
        if (fromRun.Length == 0)
            throw new DataGenerationException(
                $"No candidates for '{key}': target(s) {string.Join(", ", prepared.InRunTargets)} created no rows in this run.");
        var resolved = Array.AsReadOnly(fromRun);
        _resolved[key] = resolved;
        return resolved;
    }

    internal static async Task<PreparedLookupRun> PrepareAsync(GenerationConfig config,
        IReadOnlyDictionary<string, EntityMetadata> metadata, IReadOnlyList<string> creationOrder,
        IOrganizationServiceAsync2 service, ThrottlePolicy throttle, ILogger logger, CancellationToken ct,
        IReadOnlySet<string>? preferInRun = null)
    {
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < creationOrder.Count; i++)
            position[creationOrder[i]] = i;

        var byTarget = new Dictionary<string, IReadOnlyList<LookupRuleValue>>(StringComparer.OrdinalIgnoreCase);
        var byColumn = new Dictionary<string, PreparedLookupColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in config.EntityLogicalNames)
        {
            var count = config.PlannedRows(table);
            if (count <= 0
                || config.FieldRules is null || !config.FieldRules.TryGetValue(table, out var rules))
                continue;
            var source = metadata[table];
            var tableIndex = position.TryGetValue(table, out var ti) ? ti : int.MaxValue;
            foreach (var (column, rule) in rules)
            {
                if (rule is not LookupRandomRule) continue;
                var attr = (LookupAttributeMetadata)source.Attributes.Single(a =>
                    string.Equals(a.LogicalName, column, StringComparison.OrdinalIgnoreCase));
                var targets = attr.Targets.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray();

                // Only targets the topological order puts strictly before this table are usable: by
                // the time this table generates rows, their pool lists are complete and final.
                var inRunTargets = targets
                    .Where(t => config.PlannedRows(t) > 0
                        && position.TryGetValue(t, out var pi) && pi < tableIndex)
                    .Select(t => t.ToLowerInvariant())
                    .ToArray();

                var candidates = new List<LookupRuleValue>();
                foreach (var target in targets)
                {
                    if (!byTarget.TryGetValue(target, out var rows))
                    {
                        try
                        {
                            rows = await ReadTargetAsync(target, config.MaxRetries, metadata, service,
                                throttle, logger, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (ex is DataGenerationException or InvalidOperationException)
                        {
                            throw new DataGenerationException(
                                $"Lookup preparation failed for '{table}.{column}' ({count} rows), target '{target}': {ex.Message}", ex);
                        }
                        byTarget.Add(target, rows);
                    }
                    candidates.AddRange(rows);
                }
                var ordered = candidates.DistinctBy(v => (v.Entity, v.Id))
                    .OrderBy(v => v.Entity, StringComparer.Ordinal)
                    .ThenBy(v => v.Id.ToString("D"), StringComparer.Ordinal).ToArray();
                if (ordered.Length == 0 && inRunTargets.Length == 0)
                    throw new DataGenerationException(
                        $"No readable existing candidates for '{table}.{column}' ({count} planned rows). Choose records manually or remove this rule.");
                byColumn.Add($"{table}.{column}",
                    new PreparedLookupColumn(Array.AsReadOnly(ordered), Array.AsReadOnly(inRunTargets),
                        preferInRun?.Contains($"{table}.{column}") == true));
            }
        }
        return new(byColumn.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<LookupRuleValue>> ReadTargetAsync(string target,
        int maxRetries, IReadOnlyDictionary<string, EntityMetadata> metadata,
        IOrganizationServiceAsync2 service, ThrottlePolicy throttle, ILogger logger, CancellationToken ct)
    {
        var meta = metadata.FirstOrDefault(p =>
            string.Equals(p.Key, target, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(meta?.PrimaryIdAttribute))
        {
            var response = await throttle.ExecuteAsync(() => service.ExecuteAsync(
                new RetrieveEntityRequest { LogicalName = target, EntityFilters = EntityFilters.Entity }, ct),
                target, maxRetries, ct).ConfigureAwait(false);
            meta = ((RetrieveEntityResponse)response).EntityMetadata;
        }
        var primaryId = meta?.PrimaryIdAttribute;
        if (string.IsNullOrWhiteSpace(primaryId))
            throw new DataGenerationException($"Primary id metadata is unavailable for '{target}'.");
        var query = new QueryExpression(target)
        {
            ColumnSet = new ColumnSet(primaryId),
            PageInfo = new PagingInfo { Count = PageSize, PageNumber = 1 },
        };
        query.AddOrder(primaryId, OrderType.Ascending);
        var records = new List<LookupRuleValue>(MaximumCandidatesPerTarget);
        var more = false;
        for (var pageNumber = 1; pageNumber <= MaximumCandidatesPerTarget / PageSize; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            query.PageInfo.PageNumber = pageNumber;
            var page = await throttle.ExecuteAsync(() => service.RetrieveMultipleAsync(query, ct),
                target, maxRetries, ct).ConfigureAwait(false);
            foreach (var entity in page.Entities)
            {
                if (!string.Equals(entity.LogicalName, target, StringComparison.OrdinalIgnoreCase)
                    || entity.Id == Guid.Empty)
                    throw new DataGenerationException($"Invalid candidate identity returned for '{target}'.");
                if (records.Count < MaximumCandidatesPerTarget)
                    records.Add(new LookupRuleValue(target.ToLowerInvariant(), entity.Id));
            }
            more = page.MoreRecords;
            if (!more) break;
            query.PageInfo.PagingCookie = page.PagingCookie;
        }
        if (more)
            logger.LogWarning("Lookup candidates for {Target} are limited to the first {Limit} rows in primary-id order",
                target, MaximumCandidatesPerTarget);
        logger.LogInformation("Captured {Count} existing lookup candidates for {Target}", records.Count, target);
        return Array.AsReadOnly(records.ToArray());
    }
}

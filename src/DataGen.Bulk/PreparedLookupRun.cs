using System.Collections.Frozen;
using DataGen.Core.Contracts;
using DataGen.Core.Exceptions;
using DataGen.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DataGen.Bulk;

internal sealed class PreparedLookupRun(
    FrozenDictionary<string, IReadOnlyList<LookupRuleValue>> columns)
{
    internal const int PageSize = 500;
    internal const int MaximumCandidatesPerTarget = LookupRandomRule.MaximumCandidatesPerTarget;

    internal IReadOnlyList<LookupRuleValue> Get(string table, string column)
        => columns.TryGetValue($"{table}.{column}", out var values)
            ? values
            : throw new InvalidOperationException($"Lookup candidates were not prepared for '{table}.{column}'.");

    internal static async Task<PreparedLookupRun> PrepareAsync(GenerationConfig config,
        IReadOnlyDictionary<string, EntityMetadata> metadata, IOrganizationServiceAsync2 service,
        ThrottlePolicy throttle, ILogger logger, CancellationToken ct)
    {
        var byTarget = new Dictionary<string, IReadOnlyList<LookupRuleValue>>(StringComparer.OrdinalIgnoreCase);
        var byColumn = new Dictionary<string, IReadOnlyList<LookupRuleValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in config.EntityLogicalNames)
        {
            if (!config.RecordCounts.TryGetValue(table, out var count) || count <= 0
                || config.FieldRules is null || !config.FieldRules.TryGetValue(table, out var rules))
                continue;
            var source = metadata[table];
            foreach (var (column, rule) in rules)
            {
                if (rule is not LookupRandomRule) continue;
                var attr = (LookupAttributeMetadata)source.Attributes.Single(a =>
                    string.Equals(a.LogicalName, column, StringComparison.OrdinalIgnoreCase));
                var candidates = new List<LookupRuleValue>();
                foreach (var target in attr.Targets.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase))
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
                if (ordered.Length == 0)
                    throw new DataGenerationException(
                        $"No readable existing candidates for '{table}.{column}' ({count} planned rows). Choose records manually or remove this rule.");
                byColumn.Add($"{table}.{column}", Array.AsReadOnly(ordered));
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

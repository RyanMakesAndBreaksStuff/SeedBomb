using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using SeedBomb.Core.Contracts;
using System.Diagnostics;
using System.Reflection;
using System.Security;

namespace SeedBomb.Bulk;

/// <summary>
/// Run-level log lines for measuring a run: a snapshot of what it ran with, wall-clock intervals per
/// phase, and an optional post-run row count. Every line starts with <c>[run]</c> or <c>[timing]</c>
/// so a log can be grepped for them.
/// </summary>
internal static class RunDiagnostics
{
    private static readonly TimeSpan ReconcileTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Logs how long the phase begun at <paramref name="startedTimestamp"/> took.</summary>
    public static void LogPhase(ILogger logger, string phase, long startedTimestamp, string? entity = null) =>
        logger.LogInformation("[timing] phase {Phase} {Entity} took {ElapsedMs:F0}ms",
            phase, entity ?? "-", Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);

    /// <summary>Logs the versions and settings a run started with, once, before the first write.</summary>
    public static void LogSnapshot(
        ILogger logger, IOrganizationServiceAsync2 service, GenerationConfig config,
        IEnumerable<string> tables, Func<string, bool> attemptsCreateMultiple)
    {
        var sc = service as ServiceClient;
        var apis = string.Join(", ", tables
            .Where(t => config.PlannedRows(t) > 0)
            .Select(t => $"{t}={(attemptsCreateMultiple(t) ? "CreateMultiple" : "ExecuteMultiple")}"));
        logger.LogInformation(
            "[run] snapshot: app {AppVersion}, Dataverse.Client {SdkVersion}, Caching.Memory {CacheVersion}, batch {BatchSize}, configured DOP {ConfiguredDop}, SDK DOP hint {DopHint}, affinity cookie {AffinityCookie}, first-attempt API {Apis}",
            VersionOf(Assembly.GetEntryAssembly()),
            VersionOf(typeof(ServiceClient).Assembly),
            LoadedVersion("Microsoft.Extensions.Caching.Memory"),
            config.BatchSize,
            config.MaxParallelism?.ToString() ?? "auto",
            sc is null ? "n/a" : sc.RecommendedDegreesOfParallelism.ToString(),
            sc is null ? "n/a" : sc.EnableAffinityCookie.ToString(),
            apis);
    }

    /// <summary>
    /// When <see cref="GenerationConfig.ReconcileAfterRun"/> is set, reads each planned table's latest
    /// <c>modifiedon</c> among rows the caller created, before the run writes anything. Server time on
    /// both sides of the window, so neither clock skew nor a run that just finished can leak into the
    /// count. A table whose read fails is left out, and its reconcile line says so.
    /// </summary>
    /// <returns>Table to watermark; null when the caller has no rows in the table yet.</returns>
    public static async Task<IReadOnlyDictionary<string, DateTime?>> CaptureWatermarksAsync(
        IOrganizationServiceAsync2 service,
        GenerationConfig config,
        IReadOnlyDictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata> entityMetadata,
        CancellationToken ct)
    {
        var watermarks = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        if (!config.ReconcileAfterRun)
            return watermarks;

        foreach (var table in config.EntityLogicalNames)
        {
            if (config.PlannedRows(table) == 0 || !entityMetadata.ContainsKey(table))
                continue;
            try
            {
                // Top 1 by modifiedon, not an aggregate max: an aggregate faults past 50,000 rows.
                var query = new QueryExpression(table) { ColumnSet = new ColumnSet("modifiedon"), TopCount = 1 };
                query.Criteria.AddCondition("createdby", ConditionOperator.EqualUserId);
                query.AddOrder("modifiedon", OrderType.Descending);
                var latest = await service.RetrieveMultipleAsync(query, ct).ConfigureAwait(false);
                watermarks[table] = latest.Entities.FirstOrDefault()?.GetAttributeValue<DateTime?>("modifiedon");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Left out on purpose: ReconcileAsync reports the table as unavailable.
            }
        }

        return watermarks;
    }

    /// <summary>
    /// When <see cref="GenerationConfig.ReconcileAfterRun"/> is set, logs requested, acknowledged and
    /// observed rows per table. Observed above acknowledged means a batch committed but its response
    /// was lost. Uses its own timeout so a cancelled run is still reconciled. The watermarks come from
    /// <see cref="CaptureWatermarksAsync"/>, taken before the first write.
    /// </summary>
    public static async Task ReconcileAsync(
        ILogger logger,
        IOrganizationServiceAsync2 service,
        GenerationConfig config,
        IReadOnlyDictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata> entityMetadata,
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> created,
        IReadOnlyDictionary<string, DateTime?> watermarks)
    {
        if (!config.ReconcileAfterRun)
            return;

        using var cts = new CancellationTokenSource(ReconcileTimeout);
        foreach (var table in config.EntityLogicalNames)
        {
            var requested = config.PlannedRows(table);
            if (requested == 0 || !entityMetadata.TryGetValue(table, out var meta) || meta.PrimaryIdAttribute is null)
                continue;

            string observed;
            if (!watermarks.TryGetValue(table, out var after))
            {
                observed = "unavailable (no pre-run watermark)";
            }
            else
            {
                try
                {
                    observed = (await CountCreatedByCallerAfterAsync(service, table, meta.PrimaryIdAttribute, after, cts.Token)
                        .ConfigureAwait(false)).ToString();
                }
                catch (Exception ex)
                {
                    // An aggregate over 50,000 rows faults (AggregateQueryRecordLimit); report, don't fail the run.
                    observed = $"unavailable ({ex.GetType().Name}: {ex.Message})";
                }
            }

            logger.LogInformation(
                "[run] reconcile {Entity}: requested {Requested}, acknowledged {Acknowledged}, observed {Observed} (created by caller, written after {After})",
                table, requested, created.GetValueOrDefault(table)?.Count ?? 0, observed,
                after is { } a ? a.ToString("o") : "the caller's first row");
        }
    }

    // Filters on modifiedon, not createdon: generated rows usually set overriddencreatedon, and
    // Dataverse then stamps createdon with that (past) date, so a createdon filter finds nothing.
    // modifiedon is always the real write time. A row the caller created earlier and modified
    // during the run also counts; SeedBomb only updates rows it created in the same run.
    // ponytail: modifiedon has one-second resolution, so a row written in the watermark's own second
    // is missed. A run spends well over a second on metadata and preflight before its first write.
    private static async Task<int> CountCreatedByCallerAfterAsync(
        IOrganizationServiceAsync2 service, string table, string primaryId, DateTime? after, CancellationToken ct)
    {
        var afterCondition = after is { } a
            ? $"""<condition attribute="modifiedon" operator="gt" value="{DateTime.SpecifyKind(a, DateTimeKind.Utc):yyyy-MM-ddTHH:mm:ssZ}" />"""
            : "";
        var fetch = $"""
            <fetch aggregate="true">
              <entity name="{SecurityElement.Escape(table)}">
                <attribute name="{SecurityElement.Escape(primaryId)}" alias="n" aggregate="count" />
                <filter>
                  {afterCondition}
                  <condition attribute="createdby" operator="eq-userid" />
                </filter>
              </entity>
            </fetch>
            """;
        var result = await service.RetrieveMultipleAsync(new FetchExpression(fetch), ct).ConfigureAwait(false);
        return result.Entities.FirstOrDefault()?.GetAttributeValue<AliasedValue>("n")?.Value is int n ? n : 0;
    }

    private static string VersionOf(Assembly? assembly) =>
        assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly?.GetName().Version?.ToString()
        ?? "unknown";

    private static string LoadedVersion(string assemblyName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName) is { } loaded
            ? VersionOf(loaded)
            : "not loaded";
}

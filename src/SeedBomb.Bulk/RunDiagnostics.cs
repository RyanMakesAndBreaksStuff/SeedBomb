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

    // createdon is stamped by the server; the margin absorbs clock skew between this machine and it.
    private static readonly TimeSpan ReconcileClockMargin = TimeSpan.FromMinutes(1);

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
    /// When <see cref="GenerationConfig.ReconcileAfterRun"/> is set, logs requested, acknowledged and
    /// observed rows per table. Observed above acknowledged means a batch committed but its response
    /// was lost. Uses its own timeout so a cancelled run is still reconciled.
    /// </summary>
    public static async Task ReconcileAsync(
        ILogger logger,
        IOrganizationServiceAsync2 service,
        GenerationConfig config,
        IReadOnlyDictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata> entityMetadata,
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> created,
        DateTimeOffset runStart)
    {
        if (!config.ReconcileAfterRun)
            return;

        var since = runStart - ReconcileClockMargin;
        using var cts = new CancellationTokenSource(ReconcileTimeout);
        foreach (var table in config.EntityLogicalNames)
        {
            var requested = config.PlannedRows(table);
            if (requested == 0 || !entityMetadata.TryGetValue(table, out var meta) || meta.PrimaryIdAttribute is null)
                continue;

            string observed;
            try
            {
                observed = (await CountCreatedByCallerSinceAsync(service, table, meta.PrimaryIdAttribute, since, cts.Token)
                    .ConfigureAwait(false)).ToString();
            }
            catch (Exception ex)
            {
                // An aggregate over 50,000 rows faults (AggregateQueryRecordLimit); report, don't fail the run.
                observed = $"unavailable ({ex.GetType().Name}: {ex.Message})";
            }

            logger.LogInformation(
                "[run] reconcile {Entity}: requested {Requested}, acknowledged {Acknowledged}, observed {Observed} (created by caller since {Since:o})",
                table, requested, created.GetValueOrDefault(table)?.Count ?? 0, observed, since);
        }
    }

    private static async Task<int> CountCreatedByCallerSinceAsync(
        IOrganizationServiceAsync2 service, string table, string primaryId, DateTimeOffset since, CancellationToken ct)
    {
        var fetch = $"""
            <fetch aggregate="true">
              <entity name="{SecurityElement.Escape(table)}">
                <attribute name="{SecurityElement.Escape(primaryId)}" alias="n" aggregate="count" />
                <filter>
                  <condition attribute="createdon" operator="ge" value="{since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}" />
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

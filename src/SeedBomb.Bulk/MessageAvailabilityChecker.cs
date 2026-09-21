using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Query;
using System.Collections.Concurrent;

namespace SeedBomb.Bulk;

/// <summary>
/// Checks and caches whether the UpdateMultiple SDK message is available for a given entity,
/// and tracks which entities have rejected <c>CreateMultiple</c> at runtime.
/// </summary>
public class MessageAvailabilityChecker
{
    private readonly IOrganizationServiceAsync2 _service;
    private readonly ILogger<MessageAvailabilityChecker> _logger;
    private readonly ConcurrentDictionary<(string entityLogicalName, string messageName), bool> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageAvailabilityChecker"/> class.
    /// </summary>
    /// <param name="service">The Dataverse organization service.</param>
    /// <param name="logger">The logger instance.</param>
    public MessageAvailabilityChecker(IOrganizationServiceAsync2 service, ILogger<MessageAvailabilityChecker> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Marks <c>CreateMultiple</c> as unsupported for the given entity, overriding any cached value.
    /// Call this when a <c>CreateMultiple</c> call is rejected at runtime to prevent repeated attempts.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    public void MarkUnsupported(string entityLogicalName) =>
        _cache[(entityLogicalName, "CreateMultiple")] = false;

    /// <summary>
    /// Determines whether <c>CreateMultiple</c> should still be attempted for the given entity.
    /// Starts optimistic — the <c>sdkmessagefilter</c> probe this replaces returned false positives,
    /// so the runtime rejection caught by <see cref="BulkCreator"/> (which calls
    /// <see cref="MarkUnsupported"/>) is the only authority. In-memory only; no Dataverse round-trip.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name to check.</param>
    /// <returns>True until the entity has rejected <c>CreateMultiple</c> at runtime.</returns>
    public bool ShouldAttemptCreateMultiple(string entityLogicalName) =>
        !_cache.TryGetValue((entityLogicalName, "CreateMultiple"), out var cached) || cached;

    /// <summary>
    /// Determines whether the <c>UpdateMultiple</c> message is supported for the given entity.
    /// Result is cached for the lifetime of this instance.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if UpdateMultiple is supported; otherwise false.</returns>
    public async Task<bool> IsUpdateMultipleAvailableAsync(string entityLogicalName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        var cacheKey = (entityLogicalName, "UpdateMultiple");
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var available = await QueryMessageSupportAsync(entityLogicalName, "UpdateMultiple", ct).ConfigureAwait(false);
        _cache[cacheKey] = available;
        return available;
    }

    private async Task<bool> QueryMessageSupportAsync(string entityLogicalName, string messageName, CancellationToken ct)
    {
        try
        {
            var query = new QueryExpression("sdkmessagefilter")
            {
                ColumnSet = new ColumnSet("sdkmessagefilterid", "primaryobjecttypecode"),
                TopCount = 1,
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression(
                            "primaryobjecttypecode",
                            ConditionOperator.Equal,
                            entityLogicalName)
                    }
                }
            };

            var messageLink = query.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid");
            messageLink.LinkCriteria.AddCondition("name", ConditionOperator.Equal, messageName);

            var result = await _service.RetrieveMultipleAsync(query, ct).ConfigureAwait(false);
            return result.Entities.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If we can't query, default to safe fallback (ExecuteMultiple works everywhere)
            _logger.LogWarning(ex,
                "Failed to query {Message} support for {Entity}; defaulting to ExecuteMultiple fallback.",
                messageName, entityLogicalName);
            return false;
        }
    }
}

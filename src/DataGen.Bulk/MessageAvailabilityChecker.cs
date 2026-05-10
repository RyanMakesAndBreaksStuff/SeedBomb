using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Collections.Concurrent;

namespace DataGen.Bulk;

/// <summary>
/// Checks and caches whether the CreateMultiple SDK message is available for a given entity.
/// CreateMultiple is not supported for all out-of-the-box tables (e.g. Account, Contact).
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
    /// Determines whether the <c>CreateMultiple</c> message is supported for the given entity.
    /// Result is cached for the lifetime of this instance.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if CreateMultiple is supported; false if ExecuteMultiple fallback should be used.</returns>
    public async Task<bool> IsCreateMultipleAvailableAsync(string entityLogicalName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        var cacheKey = (entityLogicalName, "CreateMultiple");
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogDebug("Cache hit for CreateMultiple availability: {Entity} = {Available}", entityLogicalName, cached);
            return cached;
        }

        var available = await QueryMessageSupportAsync(entityLogicalName, "CreateMultiple", ct).ConfigureAwait(false);
        _cache[cacheKey] = available;

        _logger.LogInformation("CreateMultiple availability for {Entity}: {Available}", entityLogicalName, available);
        return available;
    }

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

            if (result.Entities.Count > 0 && result.Entities[0].Contains("primaryobjecttypecode"))
            {
                var rawOtc = result.Entities[0]["primaryobjecttypecode"];
                _logger.LogDebug("sdkmessagefilter.primaryobjecttypecode raw for {Entity}: {RawValue} ({Type})",
                    entityLogicalName, rawOtc, rawOtc?.GetType().Name ?? "null");
            }

            return result.Entities.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If we can't query, default to safe fallback (ExecuteMultiple works everywhere)
            _logger.LogWarning(ex,
                "Failed to query CreateMultiple support for {Entity}; defaulting to ExecuteMultiple fallback.",
                entityLogicalName);
            return false;
        }
    }
}

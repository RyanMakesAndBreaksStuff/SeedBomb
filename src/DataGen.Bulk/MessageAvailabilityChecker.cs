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
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

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
    /// Determines whether the <c>CreateMultiple</c> message is supported for the given entity.
    /// Result is cached for the lifetime of this instance.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if CreateMultiple is supported; false if ExecuteMultiple fallback should be used.</returns>
    public async Task<bool> IsCreateMultipleAvailableAsync(string entityLogicalName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        if (_cache.TryGetValue(entityLogicalName, out var cached))
        {
            _logger.LogDebug("Cache hit for CreateMultiple availability: {Entity} = {Available}", entityLogicalName, cached);
            return cached;
        }

        var available = await QueryCreateMultipleSupportAsync(entityLogicalName, ct).ConfigureAwait(false);
        _cache[entityLogicalName] = available;

        _logger.LogInformation("CreateMultiple availability for {Entity}: {Available}", entityLogicalName, available);
        return available;
    }

    private async Task<bool> QueryCreateMultipleSupportAsync(string entityLogicalName, CancellationToken ct)
    {
        try
        {
            var query = new QueryExpression("sdkmessagefilter")
            {
                ColumnSet = new ColumnSet("sdkmessagefilterid"),
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
            messageLink.LinkCriteria.AddCondition("name", ConditionOperator.Equal, "CreateMultiple");

            var result = await _service.RetrieveMultipleAsync(query, ct).ConfigureAwait(false);
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

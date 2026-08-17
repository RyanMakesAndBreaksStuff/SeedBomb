using DataGen.Core.Contracts;
using DataGen.Core.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Metadata.Query;

namespace DataGen.Core.Metadata;

/// <summary>
/// Retrieves and caches Dataverse entity metadata using the ServiceClient SDK.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DataverseMetadataProvider"/> class.
/// </remarks>
/// <param name="service">The Dataverse organization service instance.</param>
/// <param name="cache">The memory cache for metadata.</param>
/// <param name="logger">The logger instance.</param>
public class DataverseMetadataProvider(
    IOrganizationServiceAsync2 service,
    IMemoryCache cache,
    ILogger<DataverseMetadataProvider> logger) : IMetadataProvider
{
    private readonly IOrganizationServiceAsync2 _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly IMemoryCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly ILogger<DataverseMetadataProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    /// <inheritdoc/>
    public async Task<EntityMetadata> GetEntityAsync(string logicalName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var cacheKey = $"entity:{logicalName}";
        if (_cache.TryGetValue(cacheKey, out EntityMetadata? cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for entity metadata: {EntityName}", logicalName);
            return cached;
        }

        _logger.LogInformation("Retrieving metadata for entity {EntityName}", logicalName);

        try
        {
            var request = new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.All,
                RetrieveAsIfPublished = true
            };

            var response = (RetrieveEntityResponse)await _service.ExecuteAsync(request, ct).ConfigureAwait(false);
            var metadata = response.EntityMetadata;

            _cache.Set(cacheKey, metadata, CacheDuration);
            return metadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to retrieve metadata for entity {EntityName}", logicalName);
            throw new SchemaException($"Failed to retrieve metadata for entity '{logicalName}'.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EntityMetadata>> GetEntitiesAsync(
        string[] logicalNames,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(logicalNames);
        var tasks = logicalNames.Select(name => GetEntityAsync(name, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EntitySummary>> ListUserEntitiesAsync(CancellationToken ct = default)
    {
        const string cacheKey = "entity:list:user";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<EntitySummary>? cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for user entity list");
            return cached;
        }

        _logger.LogInformation("Retrieving user entity list from Dataverse");

        try
        {
            var request = new RetrieveMetadataChangesRequest
            {
                Query = new EntityQueryExpression
                {
                    Properties = new MetadataPropertiesExpression(
                        "LogicalName", "DisplayName", "IsCustomEntity",
                        "OwnershipType", "IsIntersect", "IsValidForAdvancedFind"),
                    Criteria = new MetadataFilterExpression(LogicalOperator.And)
                    {
                        Conditions =
                        {
                            new MetadataConditionExpression(
                                "IsValidForAdvancedFind",
                                MetadataConditionOperator.Equals,
                                true)
                        }
                    }
                }
            };

            var response = (RetrieveMetadataChangesResponse)await _service
                .ExecuteAsync(request, ct).ConfigureAwait(false);

            var summaries = response.EntityMetadata
                .Where(e => e.OwnershipType is OwnershipTypes.UserOwned or OwnershipTypes.OrganizationOwned)
                .Where(e => e.IsIntersect != true)
                .Select(e => new EntitySummary(
                    e.LogicalName,
                    e.DisplayName?.UserLocalizedLabel?.Label ?? e.LogicalName,
                    EntitySummary.IsUserCreated(e.LogicalName, e.IsCustomEntity == true)))
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();

            _cache.Set(cacheKey, summaries, CacheDuration);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Found {EntityCount} user entities", summaries.Count);
            }
            return summaries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to retrieve user entity list");
            throw new SchemaException("Failed to retrieve user entity list.", ex);
        }
    }
}

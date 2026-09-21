using SeedBomb.Bulk;
using SeedBomb.Core.Metadata;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Globalization;

namespace SeedBomb.Services.Dataverse;

/// <summary>Metadata-aware, cancellable lookup reads through the cached connection.</summary>
/// <param name="connection">Existing cached connection owner.</param>
/// <param name="metadata">Existing environment metadata cache.</param>
/// <param name="throttle">Shared repository retry implementation.</param>
public sealed class LookupRecordSource(
    IDataverseConnectionService connection,
    IMetadataProvider metadata,
    ThrottlePolicy throttle) : ILookupRecordSource
{
    /// <summary>Picker rows fetched per forward page.</summary>
    public const int PageSize = 100;

    private const int MaximumSearchLength = 200;
    private const int ReadRetries = 3;

    /// <inheritdoc />
    public async Task<LookupRecordPage> ReadAsync(LookupSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!(request.Attribute.Targets ?? []).Contains(request.Target, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose one of this lookup's allowed target tables.");
        if (request.PageNumber < 1 || request.Text.Length > MaximumSearchLength)
            throw new InvalidOperationException("Invalid page or search text is longer than 200 characters.");
        if (request.PageNumber > 1 && string.IsNullOrWhiteSpace(request.PagingCookie))
            throw new InvalidOperationException("This query cannot continue with a paging cookie. Refine the search.");

        var service = await connection.GetOrganizationServiceAsync(ct).ConfigureAwait(false);
        var meta = await throttle.ExecuteAsync(() => metadata.GetEntityAsync(request.Target, ct),
            request.Target, ReadRetries, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(meta.PrimaryIdAttribute))
            throw new InvalidOperationException("The target table has no readable primary-id metadata.");
        var readable = (meta.Attributes ?? []).Where(a => a.IsValidForRead == true)
            .Select(a => a.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primaryName = meta.PrimaryNameAttribute;
        var hasName = !string.IsNullOrWhiteSpace(primaryName) && readable.Contains(primaryName);
        var columns = new List<string> { meta.PrimaryIdAttribute };
        if (hasName) columns.Add(primaryName!);
        if (readable.Contains("createdon")) columns.Add("createdon");
        if (readable.Contains("createdby")) columns.Add("createdby");
        var query = new QueryExpression(request.Target)
        {
            ColumnSet = new ColumnSet(columns.ToArray()),
            PageInfo = new PagingInfo
            {
                Count = PageSize,
                PageNumber = request.PageNumber,
                PagingCookie = request.PagingCookie,
            },
        };
        query.AddOrder(meta.PrimaryIdAttribute, OrderType.Ascending);
        var text = request.Text.Trim();
        if (Guid.TryParse(text, out var id))
        {
            if (id == Guid.Empty) throw new InvalidOperationException("Enter a non-empty record GUID.");
            query.Criteria.AddCondition(meta.PrimaryIdAttribute, ConditionOperator.Equal, id);
        }
        else if (text.Length > 0)
        {
            if (!hasName)
                throw new InvalidOperationException(
                    "This table has no readable primary name. Search by full GUID or browse.");
            var literalPrefix = text.Replace("[", "[[]", StringComparison.Ordinal)
                .Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal);
            query.Criteria.AddCondition(primaryName!, ConditionOperator.Like, literalPrefix + "%");
        }

        var page = await throttle.ExecuteAsync(() => service.RetrieveMultipleAsync(query, ct),
            request.Target, ReadRetries, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var records = new List<LookupRecord>(page.Entities.Count);
        foreach (var entity in page.Entities)
        {
            if (entity.Id == Guid.Empty ||
                !string.Equals(entity.LogicalName, request.Target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Dataverse returned a record outside the requested target.");
            var name = hasName ? entity.GetAttributeValue<string>(primaryName!) : null;
            var created = entity.GetAttributeValue<DateTime?>("createdon");
            var creator = entity.GetAttributeValue<EntityReference>("createdby");
            var createdText = created is { } date
                ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
                    .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                : "—";
            var creatorText = creator is null ? "—" : creator.Name ?? creator.Id.ToString("D");
            records.Add(new(new(request.Target.ToLowerInvariant(), entity.Id, name), createdText, creatorText));
        }

        return new(records.AsReadOnly(), page.MoreRecords, page.PagingCookie);
    }
}
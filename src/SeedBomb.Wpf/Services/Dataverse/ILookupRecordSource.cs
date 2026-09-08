using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;

namespace Seedbomb.Services.Dataverse;

/// <summary>A page request scoped to the edited lookup's live allowed targets.</summary>
/// <param name="Attribute">Lookup metadata captured for the editor session.</param>
/// <param name="Target">One allowed target.</param>
/// <param name="Text">Empty browse, name prefix, or a complete GUID.</param>
/// <param name="PageNumber">One-based forward page number.</param>
/// <param name="PagingCookie">Unmodified cookie from the previous page.</param>
public sealed record LookupSearchRequest(LookupAttributeMetadata Attribute, string Target,
    string Text, int PageNumber = 1, string? PagingCookie = null);

/// <summary>One projected picker row; only Value is carried into a saved rule.</summary>
/// <param name="Value">Reference identity and display hint.</param>
/// <param name="CreatedOn">UTC creation display text, or an em dash.</param>
/// <param name="CreatedBy">Best-effort creator name/id, or an em dash.</param>
public sealed record LookupRecord(LookupRuleValue Value, string CreatedOn, string CreatedBy)
{
    /// <summary>Readable row label with a full-id fallback.</summary>
    public string Label => string.IsNullOrWhiteSpace(Value.Name)
        ? $"{Value.Entity} · {Value.Id:D}" : $"{Value.Name} · {Value.Entity} · {Value.Id:D}";
}

/// <summary>One bounded result page.</summary>
/// <param name="Records">Owned read-only projected rows.</param>
/// <param name="MoreRecords">Whether Dataverse reports more matches.</param>
/// <param name="PagingCookie">Unmodified cookie, when available.</param>
public sealed record LookupRecordPage(IReadOnlyList<LookupRecord> Records,
    bool MoreRecords, string? PagingCookie);

/// <summary>Reads lookup candidates without exposing SDK entities to the picker.</summary>
public interface ILookupRecordSource
{
    /// <summary>Reads a validated page; cancellation propagates and faults remain actionable.</summary>
    Task<LookupRecordPage> ReadAsync(LookupSearchRequest request, CancellationToken ct);
}

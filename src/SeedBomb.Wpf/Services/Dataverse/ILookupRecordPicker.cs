using SeedBomb.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;

namespace Seedbomb.Services.Dataverse;

/// <summary>Edits lookup selection without changing a rule until the caller accepts the result.</summary>
public interface ILookupRecordPicker
{
    /// <summary>Returns a detached selection on Add, or null on Cancel.</summary>
    Task<IReadOnlyList<LookupRuleValue>?> PickAsync(LookupAttributeMetadata attribute,
        IReadOnlyList<LookupRuleValue> initial, bool single, CancellationToken ct);
}
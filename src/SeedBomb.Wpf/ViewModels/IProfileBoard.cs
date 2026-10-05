using Microsoft.Xrm.Sdk.Metadata;
using SeedBomb.Services.Profiles;

namespace SeedBomb.ViewModels;

/// <summary>
/// WR-001: what the Profiles page needs from the Generate board. Implemented by
/// <see cref="GenerateViewModel"/> and injected, so the coupling lives in DI, not page code-behind.
/// </summary>
public interface IProfileBoard
{
    /// <summary>Live table metadata the board has loaded, for import validation.</summary>
    IReadOnlyDictionary<string, EntityMetadata> EntityMetadataMap { get; }

    /// <summary>The board's run id, for pattern worst-case length during import validation.</summary>
    string RunId { get; }

    /// <summary>Fetches live metadata for <paramref name="logicalNames"/> not yet loaded.</summary>
    /// <param name="logicalNames">Table logical names.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnsureMetadataAsync(IEnumerable<string> logicalNames, CancellationToken ct = default);

    /// <summary>Snapshots the board as a profile named <paramref name="name"/>.</summary>
    /// <param name="name">Profile name.</param>
    Profile BuildProfileSnapshot(string name);

    /// <summary>True when the board has unsaved rule edits.</summary>
    bool IsBoardDirty();

    /// <summary>Loads a validated import onto the board, or queues it while a run is in flight.</summary>
    /// <param name="report">The validated import.</param>
    void ApplyImportReport(ProfileImportReport report);

    /// <summary>Applies a Rules-page save when the board holds that profile.</summary>
    /// <param name="profile">The saved profile.</param>
    void ApplySavedProfileIfActive(Profile profile);
}

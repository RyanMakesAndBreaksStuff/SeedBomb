namespace Seedbomb.Services.Profiles;

/// <summary>
/// Stores and retrieves rule profiles under <c>%LOCALAPPDATA%\DataGen\profiles</c> (§3.6).
/// Load/Import validate profile schema v1 structurally only (§08 layer 1 — JSON well-formed,
/// required fields, unique names, allowed rule shapes). Neither method runs metadata validation
/// against a connected environment; that is layer 2, owned by the caller (Task 11).
/// </summary>
public interface IProfileService
{
    /// <summary>Names of all saved profiles (sanitized file stems), excluding the autosave draft.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);

    /// <summary>Writes <paramref name="profile"/> to <c>{kebab(profile.Name)}.profile.json</c>.</summary>
    Task SaveAsync(Profile profile, CancellationToken ct = default);

    /// <summary>Loads and schema-validates the profile named <paramref name="name"/>.</summary>
    Task<Profile> LoadAsync(string name, CancellationToken ct = default);

    /// <summary>Deletes the profile named <paramref name="name"/>, if it exists.</summary>
    Task DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>Loads <paramref name="name"/> and saves it again under <paramref name="newName"/>.</summary>
    Task<Profile> DuplicateAsync(string name, string newName, CancellationToken ct = default);

    /// <summary>Copies the stored file for <paramref name="name"/> to <paramref name="destPath"/>.</summary>
    Task ExportAsync(string name, string destPath, CancellationToken ct = default);

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into the store after schema-only validation (§08 layer 1).
    /// Returns the parsed profile on success, or a single plain-language error
    /// ("not a valid profile: …") and leaves the store untouched on failure. Never runs metadata
    /// validation — that is Task 11's job, against the connected environment.
    /// </summary>
    Task<(Profile? Profile, string? Error)> ImportAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>Autosaves the working draft to the fixed <c>draft.profile.json</c> slot.</summary>
    Task SaveDraftAsync(Profile profile, CancellationToken ct = default);

    /// <summary>Loads the autosaved draft, or <see langword="null"/> if absent or unreadable.</summary>
    Task<Profile?> LoadDraftAsync(CancellationToken ct = default);
}

using SeedBomb.Core.Rules;
using SeedBomb.Services.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SeedBomb.Services.Profiles;

/// <summary>
/// JSON-backed <see cref="IProfileService"/>. Profiles live under
/// <c>%LOCALAPPDATA%\SeedBomb\profiles\*.profile.json</c> (§3.6); the fixed draft slot is
/// <c>draft.profile.json</c>. Thread-safe via <see cref="SemaphoreSlim"/>
/// (<see cref="Services.Settings.JsonSettingsService"/> convention); serialization uses
/// <see cref="FieldRule.JsonOptions"/> (<c>JsonSerializerDefaults.Web</c> +
/// <c>JsonUnmappedMemberHandling.Disallow</c>) so the same options govern rule objects and
/// their containing profile.
/// </summary>
public sealed class JsonProfileService : IProfileService, IDisposable
{
    private const string DraftFileName = "draft.profile.json";
    private const string FileSuffix = ".profile.json";
    private const int MaxProfileBytes = 1024 * 1024; // §08: read at most 1 MiB per file.

    private static readonly string DefaultRoot = Path.Combine(AppPaths.Root, "profiles");

    // Property names never allowed anywhere in a profile document (D2 — no secrets by schema).
    // Scanned against JSON *property names* only, during the raw walk below — never against
    // string values, so a pattern template containing the literal word "password" is untouched.
    private static readonly HashSet<string> ReservedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "connectionString", "clientSecret", "secret", "password", "token",
    };

    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Stores profiles under <c>%LOCALAPPDATA%\SeedBomb\profiles</c>.</summary>
    public JsonProfileService() : this(DefaultRoot)
    {
    }

    /// <summary>Stores profiles under <paramref name="storageRoot"/> — test isolation overload.</summary>
    public JsonProfileService(string storageRoot)
    {
        _root = storageRoot;
    }

    private string DraftPath => Path.Combine(_root, DraftFileName);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_root))
                return [];

            return Directory.EnumerateFiles(_root, "*" + FileSuffix)
                .Select(Path.GetFileName)
                .Where(f => f is not null && !string.Equals(f, DraftFileName, StringComparison.OrdinalIgnoreCase))
                .Select(f => f![..^FileSuffix.Length])
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(Profile profile, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveLockedAsync(profile, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Save body — assumes <see cref="_lock"/> is already held. Rejects the write if the target
    /// slug file already belongs to a differently-named profile (kebab slugging is many-to-one,
    /// e.g. "Acme Sales" and "ACME   Sales" both resolve to <c>acme-sales.profile.json</c>) so a
    /// name collision never silently destroys another profile; re-saving a profile under its own
    /// unchanged name still overwrites normally.
    /// </summary>
    private async Task SaveLockedAsync(Profile profile, CancellationToken ct)
    {
        var path = ResolvePath(profile.Name);
        var collision = await FindSlugCollisionAsync(path, profile.Name, ct).ConfigureAwait(false);
        if (collision is not null)
            throw new InvalidOperationException(collision);

        Directory.CreateDirectory(_root);
        // The app always writes the current profileVersion regardless of what was passed in
        // (§08 invariant: "load → save is idempotent").
        var canonical = profile with { ProfileVersion = Profile.CurrentProfileVersion };
        var json = JsonSerializer.Serialize(canonical, FieldRule.JsonOptions);
        await AtomicFile.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Profile> LoadAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadLockedAsync(name, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Load body — assumes <see cref="_lock"/> is already held.</summary>
    private async Task<Profile> LoadLockedAsync(string name, CancellationToken ct)
    {
        var path = ResolvePath(name);
        var (bytes, boundsError) = await ReadBoundedAsync(path, ct).ConfigureAwait(false);
        if (bytes is null)
            throw new InvalidDataException(boundsError);

        var (profile, error) = TryParse(bytes);
        if (profile is null)
            throw new InvalidDataException(error);
        return profile;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = ResolvePath(name);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Profile> DuplicateAsync(string name, string newName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var source = await LoadLockedAsync(name, ct).ConfigureAwait(false);
            var copy = source with { Name = newName };
            await SaveLockedAsync(copy, ct).ConfigureAwait(false);
            return copy;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ExportAsync(string name, string destPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = ResolvePath(name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"profile \"{name}\" was not found", path);

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(path, destPath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<(Profile? Profile, string? Error)> ImportAsync(string sourcePath, CancellationToken ct = default)
    {
        var (bytes, boundsError) = await ReadBoundedAsync(sourcePath, ct).ConfigureAwait(false);
        if (bytes is null)
            return (null, boundsError);

        var (profile, error) = TryParse(bytes);
        if (profile is null)
            return (null, error);

        // Canonicalize to current profileVersion on import. Same slug-collision guard as Save —
        // two different display names can kebab to one file; never silently destroy the other.
        string destPath;
        try
        {
            destPath = ResolvePath(profile.Name);
        }
        catch (ArgumentException ex)
        {
            return (null, $"not a valid profile: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return (null, $"not a valid profile: {ex.Message}");
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collision = await FindSlugCollisionAsync(destPath, profile.Name, ct).ConfigureAwait(false);
            if (collision is not null)
                return (null, $"not a valid profile: {collision}");

            Directory.CreateDirectory(_root);
            var canonical = profile with { ProfileVersion = Profile.CurrentProfileVersion };
            var json = JsonSerializer.Serialize(canonical, FieldRule.JsonOptions);
            await AtomicFile.WriteAllTextAsync(destPath, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        return (profile, null);
    }

    /// <inheritdoc />
    public async Task SaveDraftAsync(Profile profile, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var canonical = profile with { ProfileVersion = Profile.CurrentProfileVersion };
            var json = JsonSerializer.Serialize(canonical, FieldRule.JsonOptions);
            await AtomicFile.WriteAllTextAsync(DraftPath, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Profile?> LoadDraftAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(DraftPath))
                return null;

            var (bytes, boundsError) = await ReadBoundedAsync(DraftPath, ct).ConfigureAwait(false);
            if (bytes is null)
                return null; // corrupt/oversized autosave — treat as absent rather than block startup

            var (profile, _) = TryParse(bytes);
            return profile; // malformed draft is also treated as absent, same reasoning
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearDraftAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(DraftPath))
                File.Delete(DraftPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// Returns a plain-language error if <paramref name="path"/> already holds a profile whose
    /// display <c>Name</c> differs from <paramref name="incomingName"/> (kebab slugs are many-to-one).
    /// Same-name overwrite is allowed. Corrupt/unreadable existing files are treated as free slots.
    /// Assumes <see cref="_lock"/> is held by the caller when the path is under <see cref="_root"/>.
    /// </summary>
    private async Task<string?> FindSlugCollisionAsync(string path, string incomingName, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        var (existingBytes, _) = await ReadBoundedAsync(path, ct).ConfigureAwait(false);
        if (existingBytes is null)
            return null;

        var (existingProfile, _) = TryParse(existingBytes);
        if (existingProfile is not null && existingProfile.Name != incomingName)
        {
            return
                $"a different profile named \"{existingProfile.Name}\" already uses this file name — rename the profile and try again";
        }

        return null;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a sanitized path under <see cref="_root"/> and verifies
    /// the resolved path did not escape the storage root — a path-traversal trust boundary that
    /// must hold regardless of what the sanitizer produces.
    /// </summary>
    private string ResolvePath(string name)
    {
        var slug = Sanitize(name);
        if (slug.Length == 0)
            throw new ArgumentException("name must contain at least one letter or digit", nameof(name));
        // "draft" is the fixed autosave slot (draft.profile.json); never a user profile name.
        if (string.Equals(slug, "draft", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("the name \"draft\" is reserved for the autosave slot");

        var rootFull = Path.GetFullPath(_root);
        var fullPath = Path.GetFullPath(Path.Combine(rootFull, slug + FileSuffix));
        if (!fullPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("resolved profile path escapes the storage root");

        return fullPath;
    }

    /// <summary>True when <paramref name="name"/> is its own file stem (already lower-kebab), so the
    /// profile's name and file name can never drift apart. "draft" is the reserved autosave slot.</summary>
    internal static bool IsValidName(string name) =>
        name.Length > 0 && Sanitize(name) == name && name != "draft";

    /// <summary>Lower-kebab sanitizer: "Acme Sales Scenario" → "acme-sales-scenario".</summary>
    internal static string Sanitize(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lowered.Length);
        var lastWasDash = false;
        foreach (var c in lowered)
        {
            if (c < 128 && char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        if (sb.Length > 0 && sb[^1] == '-')
            sb.Length--;
        return sb.ToString();
    }

    private static async Task<(byte[]? Bytes, string? Error)> ReadBoundedAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return (null, $"not a valid profile: file \"{path}\" was not found");
        if (info.Length > MaxProfileBytes)
            return (null, "not a valid profile: file exceeds the 1 MiB size limit");

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return (bytes, null);
    }

    /// <summary>
    /// Schema stage (§08 layer 1) — structural only, needs no connected environment.
    /// 1) Walk the raw JSON once to reject duplicate property names and reserved property names
    ///    (case-insensitive, property names only — <see cref="JsonSerializer"/> alone cannot
    ///    detect duplicate keys). 2) Deserialize with <see cref="FieldRule.JsonOptions"/>
    ///    (unmapped members disallowed). 3) Validate required fields, profileVersion 1 or 2, and
    ///    unique table/column names.
    /// </summary>
    private static (Profile? Profile, string? Error) TryParse(byte[] bytes)
    {
        var schemaError = FindSchemaError(bytes);
        if (schemaError is not null)
            return (null, schemaError);

        Profile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<Profile>(bytes, FieldRule.JsonOptions);
        }
        catch (JsonException)
        {
            return (null, "not a valid profile: malformed JSON");
        }
        catch (NotSupportedException)
        {
            return (null, "not a valid profile: malformed JSON");
        }

        if (profile is null)
            return (null, "not a valid profile: empty document");
        if (profile.ProfileVersion > Profile.CurrentProfileVersion)
            return (null,
                $"not a valid profile: created by a newer version of the app (profileVersion {profile.ProfileVersion})");
        if (profile.ProfileVersion is not (1 or 2))
            return (null, "not a valid profile: unsupported profileVersion");
        if (profile.ProfileVersion == 1 && ContainsBogusRule(profile))
            return (null, "not a valid profile: op \"bogus\" requires profileVersion 2");
        if (profile.ProfileVersion == 1)
            profile = profile with { ProfileVersion = Profile.CurrentProfileVersion };
        if (string.IsNullOrWhiteSpace(profile.Name))
            return (null, "not a valid profile: name is required");
        if (profile.Tables is null || profile.Tables.Count == 0)
            return (null, "not a valid profile: at least one table is required");

        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in profile.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Table))
                return (null, "not a valid profile: table name is required");
            if (table.Count < 1)
                return (null, $"not a valid profile: table \"{table.Table}\" count must be at least 1");
            if (!tableNames.Add(table.Table))
                return (null, $"not a valid profile: duplicate table \"{table.Table}\"");

            if (table.Columns is null)
                continue;
            foreach (var columnName in table.Columns.Keys)
            {
                if (string.IsNullOrWhiteSpace(columnName))
                    return (null, $"not a valid profile: column name is required in table \"{table.Table}\"");
            }
        }

        return (profile, null);
    }

    private static bool ContainsBogusRule(Profile profile)
    {
        foreach (var table in profile.Tables)
        {
            if (table.Columns is null)
                continue;
            foreach (var rule in table.Columns.Values)
            {
                if (rule is BogusRule)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One pass over the raw bytes with <see cref="Utf8JsonReader"/>: tracks one case-insensitive
    /// property-name set per open object (push on <c>{</c>, pop on <c>}</c>) so a duplicate or
    /// reserved key is caught regardless of nesting depth — this is what makes duplicate column
    /// keys inside a "columns" object a rejection (columns are keyed by column logical name), and
    /// what keeps reserved-word matching off string values entirely (only PropertyName tokens are
    /// ever compared).
    /// </summary>
    private static string? FindSchemaError(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            var objectStack = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectStack.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        break;
                    case JsonTokenType.EndObject:
                        if (objectStack.Count > 0)
                            objectStack.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString() ?? string.Empty;
                        if (ReservedPropertyNames.Contains(name))
                            return $"not a valid profile: property name \"{name}\" is reserved and not allowed";
                        if (objectStack.Count > 0 && !objectStack.Peek().Add(name))
                            return $"not a valid profile: duplicate property \"{name}\"";
                        break;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return "not a valid profile: malformed JSON";
        }
    }
}
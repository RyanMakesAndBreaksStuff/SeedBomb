using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Seedbomb.Services.Connections;

/// <summary>
/// JSON-backed implementation of <see cref="IConnectionProfileService"/>.
/// Profiles are stored in <c>%LOCALAPPDATA%\DataGen\connections.json</c>.
/// Sensitive fields (client secret) are encrypted with DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>) before being written to disk.
/// </summary>
public sealed class JsonConnectionProfileService : IConnectionProfileService, IDisposable
{
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DataGen", "connections.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private StoreDto? _cache;

    /// <inheritdoc/>
    public event EventHandler? ProfilesChanged;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
    {
        var store = await LoadAsync(ct).ConfigureAwait(false);
        return store.Profiles.Select(Decrypt).ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task SaveAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadLockedAsync(ct).ConfigureAwait(false);
            var idx = store.Profiles.FindIndex(p => p.Id == profile.Id);
            var dto = Encrypt(profile);
            if (idx >= 0) store.Profiles[idx] = dto;
            else store.Profiles.Add(dto);
            await PersistAsync(store, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadLockedAsync(ct).ConfigureAwait(false);
            store.Profiles.RemoveAll(p => p.Id == id);
            if (store.LastUsedId == id) store.LastUsedId = null;
            await PersistAsync(store, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public async Task<ConnectionProfile?> GetLastUsedAsync(CancellationToken ct = default)
    {
        var store = await LoadAsync(ct).ConfigureAwait(false);
        if (store.LastUsedId is null) return store.Profiles.Select(Decrypt).FirstOrDefault();
        var dto = store.Profiles.FirstOrDefault(p => p.Id == store.LastUsedId);
        return dto is null ? null : Decrypt(dto);
    }

    /// <inheritdoc/>
    public async Task SetLastUsedAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadLockedAsync(ct).ConfigureAwait(false);
            store.LastUsedId = id;
            await PersistAsync(store, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private async Task<StoreDto> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadLockedAsync(ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private async Task<StoreDto> LoadLockedAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(StoragePath))
        {
            _cache = new StoreDto();
            return _cache;
        }
        var json = await File.ReadAllTextAsync(StoragePath, ct).ConfigureAwait(false);
        json = CoerceLegacyAuthJson(json);
        _cache = JsonSerializer.Deserialize<StoreDto>(json, JsonOpts) ?? new StoreDto();
        foreach (var profile in _cache.Profiles)
        {
            // Legacy ROPC profiles (AuthType 2 / "UserPassword") predate WR-010b.
            // Downgrade to OAuth rather than leaving an undefined enum value.
            if (!Enum.IsDefined(profile.AuthType))
                profile.AuthType = AuthType.OAuth;
        }
        return _cache;
    }

    private async Task PersistAsync(StoreDto store, CancellationToken ct)
    {
        _cache = store;
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var json = JsonSerializer.Serialize(store, JsonOpts);
        await File.WriteAllTextAsync(StoragePath, json, ct).ConfigureAwait(false);
    }

    private static ProfileDto Encrypt(ConnectionProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        EnvironmentUrl = p.EnvironmentUrl,
        EnvironmentType = p.EnvironmentType,
        AuthType = p.AuthType,
        ClientId = p.ClientId,
        TenantId = p.TenantId,
        EncryptedClientSecret = EncryptString(p.ClientSecret),
        CertificateThumbprint = p.CertificateThumbprint,
    };

    private static ConnectionProfile Decrypt(ProfileDto d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        EnvironmentUrl = d.EnvironmentUrl,
        EnvironmentType = d.EnvironmentType,
        AuthType = d.AuthType,
        ClientId = d.ClientId,
        TenantId = d.TenantId,
        ClientSecret = DecryptString(d.EncryptedClientSecret),
        CertificateThumbprint = d.CertificateThumbprint,
    };

    /// <summary>
    /// Maps deleted ROPC and out-of-range values to <see cref="AuthType.OAuth"/>.
    /// Named <see cref="AuthType.Certificate"/> is kept. Integer 2 is Certificate after
    /// WR-010a; coerce it only when the profile cannot be a real cert (empty thumbprint).
    /// </summary>
    internal static AuthType CoerceLegacyAuthType(JsonElement authType, string? certificateThumbprint)
    {
        switch (authType.ValueKind)
        {
            case JsonValueKind.String:
            {
                var name = authType.GetString();
                if (string.Equals(name, "UserPassword", StringComparison.OrdinalIgnoreCase))
                    return AuthType.OAuth;
                if (Enum.TryParse<AuthType>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                    return parsed;
                return AuthType.OAuth;
            }
            case JsonValueKind.Number when authType.TryGetInt32(out var n):
            {
                var parsed = (AuthType)n;
                if (!Enum.IsDefined(parsed))
                    return AuthType.OAuth;
                if (n == 2 && string.IsNullOrWhiteSpace(certificateThumbprint))
                    return AuthType.OAuth;
                return parsed;
            }
            default:
                return AuthType.OAuth;
        }
    }

    internal static string CoerceLegacyAuthJson(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is not JsonObject || root["Profiles"] is not JsonArray profiles)
            return json;

        var changed = false;
        foreach (var item in profiles)
        {
            if (item is not JsonObject profile || profile["AuthType"] is null)
                continue;

            string? thumb = null;
            if (profile["CertificateThumbprint"] is JsonValue thumbVal)
                thumbVal.TryGetValue(out thumb);

            using var authDoc = JsonDocument.Parse(profile["AuthType"]!.ToJsonString());
            var coerced = CoerceLegacyAuthType(authDoc.RootElement, thumb);
            var coercedName = coerced.ToString();

            if (profile["AuthType"] is JsonValue existing
                && existing.TryGetValue<string>(out var current)
                && string.Equals(current, coercedName, StringComparison.Ordinal))
                continue;

            profile["AuthType"] = coercedName;
            changed = true;
        }

        return changed ? root.ToJsonString() : json;
    }

    private static string? EncryptString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var bytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string? DecryptString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var bytes = Convert.FromBase64String(value);
        var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    /// <inheritdoc/>
    public void Dispose() => _lock.Dispose();

    private sealed class StoreDto
    {
        public Guid? LastUsedId { get; set; }
        public List<ProfileDto> Profiles { get; set; } = [];
    }

    private sealed class ProfileDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string EnvironmentUrl { get; set; } = string.Empty;
        public EnvironmentType EnvironmentType { get; set; }
        [JsonConverter(typeof(LegacyAuthTypeConverter))]
        public AuthType AuthType { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string? EncryptedClientSecret { get; set; }
        public string? CertificateThumbprint { get; set; }
    }

    /// <summary>
    /// Lets leftover <c>"UserPassword"</c> and undefined integers deserialize instead of throwing.
    /// Integer 2 (now Certificate) is left for <see cref="CoerceLegacyAuthJson"/> to inspect the thumbprint.
    /// </summary>
    private sealed class LegacyAuthTypeConverter : JsonConverter<AuthType>
    {
        public override AuthType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var name = reader.GetString();
                if (string.Equals(name, "UserPassword", StringComparison.OrdinalIgnoreCase)
                    || !Enum.TryParse<AuthType>(name, ignoreCase: true, out var parsed)
                    || !Enum.IsDefined(parsed))
                    return AuthType.OAuth;
                return parsed;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
            {
                var parsed = (AuthType)n;
                return Enum.IsDefined(parsed) ? parsed : AuthType.OAuth;
            }

            return AuthType.OAuth;
        }

        public override void Write(Utf8JsonWriter writer, AuthType value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

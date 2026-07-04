using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seedbomb.Services.Connections;

/// <summary>
/// JSON-backed implementation of <see cref="IConnectionProfileService"/>.
/// Profiles are stored in <c>%LOCALAPPDATA%\DataGen\connections.json</c>.
/// Sensitive fields (client secret, password) are encrypted with DPAPI
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
        _cache = JsonSerializer.Deserialize<StoreDto>(json, JsonOpts) ?? new StoreDto();
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
        EncryptedPassword = EncryptString(p.Password),
        Username = p.Username,
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
        Password = DecryptString(d.EncryptedPassword),
        Username = d.Username,
    };

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
        public AuthType AuthType { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string? EncryptedClientSecret { get; set; }
        public string? EncryptedPassword { get; set; }
        public string? Username { get; set; }
    }
}

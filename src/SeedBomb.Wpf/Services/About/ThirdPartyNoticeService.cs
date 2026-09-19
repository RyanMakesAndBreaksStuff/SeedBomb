using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seedbomb.Services.About;

/// <summary>Deserializes <c>Seedbomb.ThirdPartyNotices.json</c> and reads license resources.</summary>
public sealed class ThirdPartyNoticeService(IEmbeddedResourceReader resources) : IThirdPartyNoticeService
{
    internal const string ManifestName = "Seedbomb.ThirdPartyNotices.json";
    internal const string AppLicenseName = "Seedbomb.AppLicense.txt";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public ThirdPartyNoticeLoadResult Load()
    {
        var json = resources.ReadUtf8(ManifestName);
        if (json is null)
            return new([], ["Third-party notice manifest is missing."]);

        ManifestFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ManifestFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new([], [$"Third-party notice manifest is invalid: {ex.Message}"]);
        }

        var components = new List<ThirdPartyComponent>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in file?.Components ?? [])
        {
            var parsed = TryMap(row, seen, errors);
            if (parsed is not null)
                components.Add(parsed);
        }

        return new(components, errors);
    }

    /// <inheritdoc />
    public IReadOnlyList<ThirdPartyComponent> GetFeatured(ThirdPartyNoticeLoadResult loaded) =>
        loaded.Components
            .Where(c => c.Featured)
            .OrderBy(c => c.FeaturedOrder)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToArray();

    /// <inheritdoc />
    public string ReadLicense(ThirdPartyComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return resources.ReadUtf8(component.LicenseResource)
               ?? "License text is not available for this component.";
    }

    /// <summary>Reads the DataGen/SeedBomb project license.</summary>
    public string ReadAppLicense() =>
        resources.ReadUtf8(AppLicenseName)
        ?? "The application license could not be loaded.";

    private static ThirdPartyComponent? TryMap(ManifestRow row, HashSet<string> seen, List<string> errors)
    {
        var name = row.Name?.Trim();
        var version = row.Version?.Trim();
        var license = row.License?.Trim();
        var url = row.ProjectUrl?.Trim();
        var resource = row.LicenseResource?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)
                                            || string.IsNullOrWhiteSpace(license) || string.IsNullOrWhiteSpace(url)
                                            || string.IsNullOrWhiteSpace(resource))
        {
            errors.Add($"Skipped a component with missing required fields ({name ?? "unnamed"}).");
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add($"Skipped {name}: project URL is not a safe https URL.");
            return null;
        }

        if (!resource.StartsWith("Seedbomb.Licenses.", StringComparison.Ordinal)
            || resource.Contains("..", StringComparison.Ordinal)
            || resource.IndexOfAny(['/', '\\']) >= 0)
        {
            errors.Add($"Skipped {name}: licenseResource is unsafe.");
            return null;
        }

        var key = $"{name}/{version}";
        if (!seen.Add(key))
        {
            errors.Add($"Skipped duplicate {key}.");
            return null;
        }

        return new ThirdPartyComponent(
            name, version, row.Purpose ?? "", license, row.Copyright ?? "",
            url, resource, row.Featured, string.IsNullOrWhiteSpace(row.Credit) ? null : row.Credit,
            row.FeaturedOrder);
    }

    private sealed class ManifestFile
    {
        public List<ManifestRow>? Components { get; set; }
    }

    private sealed class ManifestRow
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Purpose { get; set; }
        public string? License { get; set; }
        public string? Copyright { get; set; }
        public string? ProjectUrl { get; set; }
        public string? LicenseResource { get; set; }
        public bool Featured { get; set; }
        public string? Credit { get; set; }
        [JsonPropertyName("featuredOrder")] public int FeaturedOrder { get; set; }
    }
}
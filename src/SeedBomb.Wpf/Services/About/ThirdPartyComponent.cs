namespace SeedBomb.Services.About;

/// <summary>One third-party component shown on About or in the notices list.</summary>
public sealed record ThirdPartyComponent(
    string Name,
    string Version,
    string Purpose,
    string License,
    string Copyright,
    string ProjectUrl,
    string LicenseResource,
    bool Featured,
    string? Credit = null,
    int FeaturedOrder = 0);

/// <summary>Result of loading the embedded notice manifest.</summary>
public sealed record ThirdPartyNoticeLoadResult(
    IReadOnlyList<ThirdPartyComponent> Components,
    IReadOnlyList<string> Errors);
namespace Seedbomb.Services.About;

/// <summary>Loads third-party notice metadata and license text from embedded resources.</summary>
public interface IThirdPartyNoticeService
{
    /// <summary>Loads and validates the manifest. Malformed entries are skipped.</summary>
    ThirdPartyNoticeLoadResult Load();

    /// <summary>Returns featured components in <see cref="ThirdPartyComponent.FeaturedOrder"/>.</summary>
    IReadOnlyList<ThirdPartyComponent> GetFeatured(ThirdPartyNoticeLoadResult loaded);

    /// <summary>Reads verbatim license text for <paramref name="component"/>.</summary>
    string ReadLicense(ThirdPartyComponent component);

    /// <summary>Reads the DataGen/SeedBomb project license.</summary>
    string ReadAppLicense();
}

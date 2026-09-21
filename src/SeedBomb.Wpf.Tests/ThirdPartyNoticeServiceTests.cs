using SeedBomb;
using SeedBomb.Services.About;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class ThirdPartyNoticeServiceTests
{
    [Fact]
    public void Load_skips_malformed_rows_and_keeps_valid_ones()
    {
        var json = """
            {
              "components": [
                {
                  "name": "Bogus",
                  "version": "35.6.5",
                  "purpose": "Synthetic data generation",
                  "license": "MIT AND BSD-3-Clause",
                  "copyright": "Copyright (c) 2015 Brian Chavez",
                  "projectUrl": "https://github.com/bchavez/Bogus",
                  "licenseResource": "SeedBomb.Licenses.Bogus-LICENSE.txt",
                  "featured": true,
                  "credit": "Synthetic data generation powered by Bogus, created by Brian Chavez.",
                  "featuredOrder": 1
                },
                { "name": "Bad", "version": "1.0.0" },
                {
                  "name": "Evil",
                  "version": "1.0.0",
                  "license": "MIT",
                  "projectUrl": "http://example.com",
                  "licenseResource": "SeedBomb.Licenses.x"
                }
              ]
            }
            """;
        var resources = new FakeResources
        {
            [ThirdPartyNoticeService.ManifestName] = json,
            ["SeedBomb.Licenses.Bogus-LICENSE.txt"] = "BOGUS LICENSE TEXT",
        };
        var service = new ThirdPartyNoticeService(resources);

        var loaded = service.Load();

        Assert.Single(loaded.Components);
        Assert.Equal(2, loaded.Errors.Count);
        var featured = service.GetFeatured(loaded);
        Assert.Equal("Bogus", Assert.Single(featured).Name);
        Assert.Equal("BOGUS LICENSE TEXT", service.ReadLicense(featured[0]));
        Assert.Contains("Brian Chavez", featured[0].Credit, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_missing_manifest_does_not_throw()
    {
        var loaded = new ThirdPartyNoticeService(new FakeResources()).Load();
        Assert.Empty(loaded.Components);
        Assert.Equal("Third-party notice manifest is missing.", Assert.Single(loaded.Errors));
    }

    [Fact]
    public void Load_production_manifest_has_fifty_components_and_four_featured()
    {
        var service = new ThirdPartyNoticeService(new AssemblyResourceReader(typeof(App).Assembly));
        var loaded = service.Load();
        Assert.Empty(loaded.Errors);
        Assert.Equal(50, loaded.Components.Count);
        var featured = service.GetFeatured(loaded);
        Assert.Equal(4, featured.Count);
        Assert.Equal(["Bogus", "WPF-UI", "CommunityToolkit.Mvvm", "Microsoft.PowerPlatform.Dataverse.Client"],
            featured.Select(c => c.Name).ToArray());
        var bogus = featured[0];
        Assert.Equal("35.6.5", bogus.Version);
        var text = service.ReadLicense(bogus);
        Assert.Contains("Brian Chavez", text, StringComparison.Ordinal);
        Assert.Contains("BSD", text, StringComparison.Ordinal);
        Assert.True(text.Length > 3000);
        Assert.NotNull(typeof(App).Assembly.GetManifestResourceStream(bogus.LicenseResource));

        var dataverse = featured.Single(component =>
            component.Name == "Microsoft.PowerPlatform.Dataverse.Client");
        var dataverseText = service.ReadLicense(dataverse);
        Assert.StartsWith("MICROSOFT SOFTWARE LICENSE", dataverseText, StringComparison.Ordinal);
        Assert.DoesNotContain("slt dynamics365 sdk", dataverseText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAppLicense_returns_bsd_text()
    {
        var service = new ThirdPartyNoticeService(new AssemblyResourceReader(typeof(App).Assembly));
        var text = service.ReadAppLicense();
        Assert.StartsWith("BSD 3-Clause License", text, StringComparison.Ordinal);
    }

    private sealed class FakeResources : Dictionary<string, string>, IEmbeddedResourceReader
    {
        public string? ReadUtf8(string logicalName) =>
            TryGetValue(logicalName, out var value) ? value : null;
    }
}

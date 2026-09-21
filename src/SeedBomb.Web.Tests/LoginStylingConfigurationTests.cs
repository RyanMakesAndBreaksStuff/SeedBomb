using System.Runtime.CompilerServices;

namespace DataGen.Web.Tests;

public class LoginStylingConfigurationTests
{
    [Fact]
    public async Task StaticAssetEndpoints_AreMappedAsAnonymous()
    {
        var program = await ReadWebFileAsync("Program.cs");

        Assert.Contains("app.MapStaticAssets()", program);
        Assert.Contains("AllowAnonymousAttribute", program);
    }

    [Fact]
    public async Task RootApp_ReferencesAppCssThroughStaticAssetPipeline()
    {
        var app = await ReadWebFileAsync("Components", "App.razor");

        Assert.Contains("href=\"@Assets[\"app.css\"]\"", app);
    }

    [Fact]
    public async Task LoginLayout_RendersPageBodyInsideThemeProviders()
    {
        var layout = await ReadWebFileAsync("Components", "Layout", "LoginLayout.razor");

        Assert.Contains("<MudThemeProvider", layout);
        Assert.Contains("<MudPopoverProvider", layout);
        Assert.Contains("@Body", layout);
    }

    private static Task<string> ReadWebFileAsync(params string[] relativePath)
    {
        var path = GetWebPath(relativePath);
        return File.ReadAllTextAsync(path);
    }

    private static string GetWebPath(
        string[] relativePath,
        [CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("Unable to resolve test file directory.");

        return Path.GetFullPath(
            Path.Combine([testDirectory, "..", "DataGen.Web", .. relativePath]));
    }
}

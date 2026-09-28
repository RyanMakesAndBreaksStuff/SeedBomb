using System.Runtime.CompilerServices;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class SplashWindowAssetTests
{
    [Fact]
    public void ProjectPackagesCustomSplashResources()
    {
        var project = ReadRepoFile("src/SeedBomb.Wpf/SeedBomb.Wpf.csproj");
        Assert.Contains("<ApplicationIcon>Resources\\logo.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains("<Resource Include=\"Resources\\logo.ico\" />", project, StringComparison.Ordinal);
        Assert.Contains("<Resource Include=\"Resources\\logo-dark.ico\" />", project, StringComparison.Ordinal);
        Assert.Contains("<Resource Include=\"Resources\\logo-dark.png\" />", project, StringComparison.Ordinal);
        Assert.Contains("<Resource Include=\"Resources\\logo-light.png\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<SplashScreen Include=", project, StringComparison.Ordinal);
        Assert.DoesNotContain("SB_logo", project, StringComparison.Ordinal);
    }

    [Fact]
    public void SplashWindowUsesPackagedLightLogo()
    {
        var splash = ReadRepoFile("src/SeedBomb.Wpf/Resources/SplashWindow.xaml");
        Assert.Contains("x:Class=\"SeedBomb.Resources.SplashWindow\"", splash, StringComparison.Ordinal);
        Assert.Contains("Source=\"/Resources/logo-light.png\"", splash, StringComparison.Ordinal);
        Assert.DoesNotContain("Source=\"Assets/", splash, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutPageUsesThemeKeyedLogo()
    {
        var about = ReadRepoFile("src/SeedBomb.Wpf/Views/Pages/AboutPage.xaml");
        var manager = ReadRepoFile("src/SeedBomb.Wpf/Services/Theme/DesignThemeManager.cs");
        Assert.DoesNotContain("logo-256.png", about, StringComparison.Ordinal);
        Assert.Contains("Source=\"{DynamicResource DG.Logo}\"", about, StringComparison.Ordinal);
        Assert.Contains("Resources/logo-light.png", manager, StringComparison.Ordinal);
        Assert.Contains("Resources/logo-dark.png", manager, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SeedBomb.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}

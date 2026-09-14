using System.Runtime.CompilerServices;
using Xunit;

namespace DataGen.Wpf.Tests;

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
        Assert.DoesNotContain("<SplashScreen Include=", project, StringComparison.Ordinal);
        Assert.DoesNotContain("SB_logo", project, StringComparison.Ordinal);
    }

    [Fact]
    public void SplashWindowUsesPackagedDarkLogo()
    {
        var splash = ReadRepoFile("src/SeedBomb.Wpf/Resources/SplashWindow.xaml");
        Assert.Contains("x:Class=\"Seedbomb.Resources.SplashWindow\"", splash, StringComparison.Ordinal);
        Assert.Contains("Source=\"pack://application:,,,/Resources/logo-dark.png\"", splash, StringComparison.Ordinal);
        Assert.DoesNotContain("Source=\"Assets/", splash, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SeedBomb.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}

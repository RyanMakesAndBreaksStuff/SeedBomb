using System.Runtime.CompilerServices;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class SplashScreenAssetTests
{
    [Fact]
    public void WpfProjectUsesNewLogoAssets()
    {
        var project = ReadRepoFile("src/SeedBomb.Wpf/SeedBomb.Wpf.csproj");

        Assert.Contains("<ApplicationIcon>Resources\\SB_logo.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains("<SplashScreen Include=\"Resources\\SB_logo_preview_256.png\" />", project, StringComparison.Ordinal);
        Assert.Contains("<Resource Include=\"Resources\\SB_logo.ico\" />", project, StringComparison.Ordinal);
        Assert.Contains("<Resource Include=\"Resources\\SB_logo_darkbg.ico\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Assets\\logos\\seedbomb", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShellAndTrayUseNewPackUris()
    {
        var shell = ReadRepoFile("src/SeedBomb.Wpf/Views/Windows/MainWindow.xaml");
        var tray = ReadRepoFile("src/SeedBomb.Wpf/Services/TrayIconService.cs");

        Assert.Contains("pack://application:,,,/Resources/SB_logo.ico", shell, StringComparison.Ordinal);
        Assert.Contains("pack://application:,,,/Resources/SB_logo.ico", tray, StringComparison.Ordinal);
        Assert.Contains("pack://application:,,,/Resources/SB_logo_darkbg.ico", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("seedbomb_tray_", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void SplashSamplesReferencePreviewLogo()
    {
        var samples = ReadRepoFile("src/SeedBomb.Wpf/Resources/SplashScreenSamples.md");

        Assert.Contains("SB_logo_preview_256.png", samples, StringComparison.Ordinal);
        Assert.Contains("pack://application:,,,/Resources/SB_logo.ico", samples, StringComparison.Ordinal);
        Assert.Contains("pack://application:,,,/Resources/SB_logo_darkbg.ico", samples, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataGen.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}

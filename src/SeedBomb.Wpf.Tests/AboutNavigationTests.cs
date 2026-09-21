using System.Runtime.CompilerServices;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class AboutNavigationTests
{
    [Fact]
    public void Settings_does_not_host_about_and_shell_has_about_item()
    {
        var root = FindRepoRoot();
        var settings = File.ReadAllText(Path.Combine(root, "src/SeedBomb.Wpf/Views/Pages/SettingsPage.xaml"));
        var shell = File.ReadAllText(Path.Combine(root, "src/SeedBomb.Wpf/Views/Windows/MainWindow.xaml"));
        var vm = File.ReadAllText(Path.Combine(root, "src/SeedBomb.Wpf/ViewModels/SettingsViewModel.cs"));
        var overlay = File.ReadAllText(Path.Combine(root, "src/SeedBomb.Wpf/Views/Windows/MainWindow.xaml.cs"));

        Assert.DoesNotContain("Text=\"About\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("AppVersion", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("DotnetVersion", vm, StringComparison.Ordinal);
        Assert.Contains("Content=\"About\"", shell, StringComparison.Ordinal);
        Assert.Contains("TargetPageType=\"{x:Type pages:AboutPage}\"", shell, StringComparison.Ordinal);
        AssertOrdered(shell, "Content=\"Connections\"", "Content=\"Settings\"", "Content=\"About\"");
        Assert.Contains("is not AboutPage", overlay, StringComparison.Ordinal);

        static void AssertOrdered(string source, params string[] anchors)
        {
            var previous = -1;
            foreach (var anchor in anchors)
            {
                var current = source.IndexOf(anchor, StringComparison.Ordinal);
                Assert.True(current > previous, $"Expected '{anchor}' after the preceding item.");
                previous = current;
            }
        }
    }

    private static string FindRepoRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SeedBomb.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }
}

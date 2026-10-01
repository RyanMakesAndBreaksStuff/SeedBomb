using System.Runtime.CompilerServices;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class SplashScreenAssetTests
{
    [Fact]
    public void StartupShowsAndCompletesCustomSplash()
    {
        var app = ReadRepoFile("src/SeedBomb.Wpf/App.xaml.cs");
        Assert.Contains("using SeedBomb.Resources;", app, StringComparison.Ordinal);
        AssertOrdered(app, "splash = new SplashWindow();", "splash.Show();",
            "await _host.StartAsync();",
            "await ShowMainWindow(result.DisplayName ?? string.Empty, result.Succeeded);",
            "_host.Services.GetRequiredService<TrayIconService>();", "splash.SetProgress(1.0);");
        Assert.Contains("splash.SetStatus(\"Starting services...\");", app, StringComparison.Ordinal);
        Assert.Contains("splash.SetStatus(\"Restoring your session...\");", app, StringComparison.Ordinal);
        Assert.Contains("splash.SetStatus(\"Ready\");", app, StringComparison.Ordinal);
        // WR-002: the startup catch must leave the splash to Shutdown(1), or the exit code is 0.
        Assert.DoesNotContain("splash?.Close();", app, StringComparison.Ordinal);
        AssertOrdered(app, "Current.MainWindow = mainWindow;", "mainWindow.Show();");
    }

    [Fact]
    public void ShellAndTrayUseCanonicalPackUris()
    {
        var shell = ReadRepoFile("src/SeedBomb.Wpf/Views/Windows/MainWindow.xaml");
        var tray = ReadRepoFile("src/SeedBomb.Wpf/Services/TrayIconService.cs");
        Assert.Contains("Icon=\"pack://application:,,,/Resources/logo.ico\"", shell, StringComparison.Ordinal);
        Assert.Contains("Source=\"pack://application:,,,/Resources/logo.ico\"", shell, StringComparison.Ordinal);
        Assert.Contains("pack://application:,,,/Resources/logo.ico", tray, StringComparison.Ordinal);
        Assert.Contains("pack://application:,,,/Resources/logo-dark.ico", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeWiringContainsNoLegacyBrandReferences()
    {
        string wiring = string.Join('\n', ReadRepoFile("src/SeedBomb.Wpf/App.xaml.cs"),
            ReadRepoFile("src/SeedBomb.Wpf/SeedBomb.Wpf.csproj"),
            ReadRepoFile("src/SeedBomb.Wpf/Views/Windows/MainWindow.xaml"),
            ReadRepoFile("src/SeedBomb.Wpf/Services/TrayIconService.cs"));
        Assert.DoesNotContain("SB_logo", wiring, StringComparison.Ordinal);
        Assert.DoesNotContain("seedbomb.ico", wiring, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SplashScreenSamples", wiring, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string source, params string[] anchors)
    {
        var previous = -1;
        foreach (var anchor in anchors)
        {
            var current = source.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{anchor}' after the preceding startup step.");
            previous = current;
        }
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

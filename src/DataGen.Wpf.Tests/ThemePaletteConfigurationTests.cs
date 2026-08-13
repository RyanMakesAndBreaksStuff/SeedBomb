using System.Runtime.CompilerServices;
using Seedbomb.Services.Theme;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class ThemePaletteConfigurationTests
{
    private static readonly string[] ExpectedLightHex =
    [
        "#F5F6F8",
        "#ECEFF3",
        "#FFFFFF",
        "#151A21",
        "#606B79",
        "#D3D9E1",
        "#3E6FA8",
        "#668DBE",
        "#DCE8F5",
        "#2F7D52",
        "#A87524",
        "#B84A4A",
        "#3E75A8",
    ];

    private static readonly string[] ExpectedDarkHex =
    [
        "#101318",
        "#171C23",
        "#202733",
        "#E2E7EE",
        "#9AA6B5",
        "#303A49",
        "#78A6D8",
        "#96B9E0",
        "#21354B",
        "#68B487",
        "#D3A451",
        "#DF7772",
    ];

    private static readonly string[] RequiredBrushKeys =
    [
        "DG.Accent",
        "DG.AccentLight",
        "DG.AccentSoft",
        "DG.Success",
        "DG.Warning",
        "DG.Error",
        "DG.Info",
        "DG.SuccessSoft",
        "DG.SuccessBorder",
        "DG.WarningSoft",
        "DG.WarningBorder",
        "DG.ErrorSoft",
        "DG.ErrorBorder",
        "DG.InfoSoft",
        "DG.InfoBorder",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentTextFillColorPrimaryBrush",
        "SystemAccentColorBrush",
        "SystemAccentColor3Brush",
        "SystemFillColorSuccessBackgroundBrush",
        "SystemFillColorCriticalBackgroundBrush",
        "SystemFillColorCautionBackgroundBrush",
    ];

    private static readonly string[] LegacyPurpleAndStaticStatusHex =
    [
        "#0C0A18",
        "#16122A",
        "#201C3C",
        "#1C1834",
        "#38306A",
        "#EDE8FF",
        "#9B8EC4",
        "#5A5080",
        "#F3F0FC",
        "#DDD5F5",
        "#1A0F3C",
        "#6B5B8A",
        "#9E8EC4",
        "#7C3AED",
        "#9461F7",
        "#1F7C3AED",
        "#059669",
        "#D97706",
        "#DC2626",
        "#1FDC2626",
        "#40DC2626",
        "#1A059669",
        "#40059669",
        "#14059669",
        "#33059669",
    ];

    [Fact]
    public void DrawerAnimationDurationIsZeroWhenReduceMotion()
    {
        DesignThemeManager.ReduceMotion = true;
        Assert.Equal(TimeSpan.Zero, DesignThemeManager.DrawerAnimationDuration);

        DesignThemeManager.ReduceMotion = false;
        Assert.Equal(TimeSpan.FromMilliseconds(250), DesignThemeManager.DrawerAnimationDuration);
    }

    [Fact]
    public void SharedResourcesDefineSlateSteelLightDefaults()
    {
        var sharedResources = ReadRepoFile("src/DataGen.Wpf/Resources/Shared.xaml");

        foreach (var color in ExpectedLightHex)
        {
            Assert.Contains(color, sharedResources, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RuntimeThemeManagerDefinesSlateSteelPalettes()
    {
        var manager = ReadRepoFile("src/DataGen.Wpf/Services/Theme/DesignThemeManager.cs");

        foreach (var color in ExpectedLightHex.Concat(ExpectedDarkHex).Select(color => ToRgbLiteral(color)))
        {
            Assert.Contains(color, manager, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ThemeResourcesExposeDynamicAccentAndStatusBrushes()
    {
        var sharedResources = ReadRepoFile("src/DataGen.Wpf/Resources/Shared.xaml");
        var manager = ReadRepoFile("src/DataGen.Wpf/Services/Theme/DesignThemeManager.cs");

        foreach (var key in RequiredBrushKeys)
        {
            Assert.Contains($"x:Key=\"{key}\"", sharedResources, StringComparison.Ordinal);
            Assert.Contains($"\"{key}\"", manager, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WpfThemeSurfaceDoesNotKeepLegacyPurpleOrStaticStatusOverlays()
    {
        var themedFiles = new[]
        {
            "src/DataGen.Wpf/Resources/Shared.xaml",
            "src/DataGen.Wpf/Services/Theme/DesignThemeManager.cs",
            "src/DataGen.Wpf/Resources/History.xaml",
            "src/DataGen.Wpf/Views/Pages/GeneratePage.xaml",
        };

        var combined = string.Join(Environment.NewLine, themedFiles.Select(path => ReadRepoFile(path)));

        foreach (var color in LegacyPurpleAndStaticStatusHex)
        {
            Assert.DoesNotContain(color, combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ToRgbLiteral(color), combined, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataGen.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }

    private static string ToRgbLiteral(string hex) =>
        $"0x{hex.TrimStart('#').ToUpperInvariant()}";
}

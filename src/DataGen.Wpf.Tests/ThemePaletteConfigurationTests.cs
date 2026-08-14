using System.Runtime.CompilerServices;
using Seedbomb.Services.Theme;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class ThemePaletteConfigurationTests
{
    private static readonly string[] NewPaletteIds =
        ["violet-ink", "graphite", "tidewater", "terracotta"];

    private static readonly string[] RetiredPaletteIds =
        ["slate-steel", "deep-ocean", "ember-forge", "midnight-orchid", "arctic-moss"];

    private static readonly (string Old, string New)[] Migration =
    [
        ("slate-steel", "graphite"),
        ("deep-ocean", "tidewater"),
        ("ember-forge", "terracotta"),
        ("midnight-orchid", "violet-ink"),
        ("arctic-moss", "tidewater"),
        ("", "violet-ink"),
        ("not-a-palette", "violet-ink"),
        ("violet-ink", "violet-ink"),
    ];

    private static readonly string[] RequiredApplyKeys =
    [
        "DG.Bg", "DG.TitleBar", "DG.Surface1", "DG.Card", "DG.CodeSurface",
        "DG.Accent", "DG.AccentHover", "DG.AccentLight", "DG.OnAccent",
        "DG.Success", "DG.Warning", "DG.Error", "DG.Info",
        "DG.SuccessSoft", "DG.WarningSoft", "DG.ErrorSoft", "DG.InfoSoft",
        "AccentBrush",
        "AccentFillColorDefaultBrush",
        "SystemAccentColorBrush",
        "SystemFillColorSuccessBackgroundBrush",
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
    public void DefaultPaletteIsVioletInkAndFourOptionsExist()
    {
        Assert.Equal("violet-ink", DesignThemeManager.DefaultPaletteId);
        Assert.Equal(NewPaletteIds, DesignThemeManager.AvailablePalettes.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void ResolvePaletteIdMapsLegacyAndUnknown()
    {
        foreach (var (oldId, expected) in Migration)
            Assert.Equal(expected, DesignThemeManager.ResolvePaletteId(oldId));
    }

    [Fact]
    public void RuntimeThemeManagerDeclaresNewPalettesAndDropsRetiredIds()
    {
        var manager = ReadRepoFile("src/DataGen.Wpf/Services/Theme/DesignThemeManager.cs");

        Assert.Contains("violet-ink", manager, StringComparison.Ordinal);
        Assert.Contains("0xA79CF1", manager, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x7160E8", manager, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReduceMotion", manager, StringComparison.Ordinal);
        Assert.Contains("DrawerAnimationDuration", manager, StringComparison.Ordinal);
        Assert.Contains("DG.AccentLight", manager, StringComparison.Ordinal);

        foreach (var id in RetiredPaletteIds)
            Assert.DoesNotContain($"[\"{id}\"]", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPublishesRequiredKeys()
    {
        var manager = ReadRepoFile("src/DataGen.Wpf/Services/Theme/DesignThemeManager.cs");
        foreach (var key in RequiredApplyKeys)
            Assert.Contains($"\"{key}\"", manager, StringComparison.Ordinal);
        Assert.Contains("Application.Current is null", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIsNoOpWhenNoApplication()
    {
        DesignThemeManager.Apply(true, "violet-ink");
        DesignThemeManager.Apply(false, "graphite");
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

using SeedBomb.Services.Theme;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class ThemePaletteConfigurationTests
{
    private static readonly string[] NewPaletteIds = ["kiln", "notes"];

    private static readonly string[] RetiredPaletteIds =
    [
        "slate-steel", "deep-ocean", "ember-forge", "midnight-orchid", "arctic-moss",
        "violet-ink", "graphite", "tidewater", "terracotta",
    ];

    private static readonly (string Old, string New)[] Migration =
    [
        ("violet-ink", "kiln"),
        ("graphite", "kiln"),
        ("tidewater", "kiln"),
        ("terracotta", "kiln"),
        ("slate-steel", "kiln"),
        ("", "kiln"),
        ("not-a-palette", "kiln"),
        ("kiln", "kiln"),
        ("notes", "notes"),
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
        "DG.Energy", "DG.Focus", "DG.SelectionIndicator",
        "DG.Series1", "DG.Series2", "DG.Series3", "DG.Series4", "DG.Series5",
        "DG.RunSweep",
    ];

    private static readonly string[] RequiredSharedKeys =
    [
        "DG.TitleBar", "DG.OnAccent", "DG.AccentLight", "DG.Info", "DG.InfoSoft",
        "DG.SuccessSoft", "DG.Type.Stat", "DG.Pad.ActionRow", "DG.Pad.IndexRow",
        "DG.Pad.RowTall", "DG.Size.NavPane", "DG.PrimaryButton", "DG.Cell", "DG.CheckBox",
        "DG.ComboBox", "DG.ComboBoxItem", "DG.ToggleButton",
        "BoolToVisibilityConverter",
        "DG.Energy", "DG.Focus", "DG.SelectionIndicator",
        "DG.Series1", "DG.Series2", "DG.Series3", "DG.Series4", "DG.Series5",
        "DG.RunSweep",
    ];

    [Fact]
    public void DefaultPaletteIsKilnAndTwoOptionsExist()
    {
        Assert.Equal("kiln", DesignThemeManager.DefaultPaletteId);
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
        var manager = ReadRepoFile("src/SeedBomb.Wpf/Services/Theme/DesignThemeManager.cs");

        Assert.Contains("kiln", manager, StringComparison.Ordinal);
        Assert.Contains("0xBADD52", manager, StringComparison.OrdinalIgnoreCase);   // Kiln dark accent
        Assert.Contains("0x262420", manager, StringComparison.OrdinalIgnoreCase);   // Kiln light accent
        Assert.Contains("DG.Energy", manager, StringComparison.Ordinal);
        Assert.Contains("DG.SelectionIndicator", manager, StringComparison.Ordinal);
        Assert.Contains("DG.Series1", manager, StringComparison.Ordinal);
        Assert.Contains("DG.Focus", manager, StringComparison.Ordinal);
        Assert.Contains("DG.RunSweep", manager, StringComparison.Ordinal);
        Assert.Contains("DG.AccentLight", manager, StringComparison.Ordinal);

        foreach (var id in RetiredPaletteIds)
            Assert.DoesNotContain($"[\"{id}\"]", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPublishesRequiredKeys()
    {
        var manager = ReadRepoFile("src/SeedBomb.Wpf/Services/Theme/DesignThemeManager.cs");
        foreach (var key in RequiredApplyKeys)
            Assert.Contains($"\"{key}\"", manager, StringComparison.Ordinal);
        Assert.Contains("!app.Dispatcher.CheckAccess()", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyIsNoOpWhenNoApplication()
    {
        DesignThemeManager.Apply(true, "violet-ink");
        DesignThemeManager.Apply(false, "graphite");
    }

    [Fact]
    public void SharedResourcesDefineKilnKeysAndConverterAliases()
    {
        var shared = ReadRepoFile("src/SeedBomb.Wpf/Resources/Shared.xaml");
        Assert.Contains("#BADD52", shared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#C9E76F", shared, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#A79CF1", shared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x:Key=\"InverseBoolToVisible\"", shared, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"EqualityToBoolConverter\"", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"BoolToVisible\"", shared, StringComparison.Ordinal);
        foreach (var key in RequiredSharedKeys)
            Assert.Contains($"x:Key=\"{key}\"", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("#3E6FA8", shared, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<SeekStoryboard", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("<BeginStoryboard", shared, StringComparison.Ordinal);
        Assert.Contains("<ControlTemplate TargetType=\"ComboBox\">", shared, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource DG.ComboBox}\"", shared, StringComparison.Ordinal);
        Assert.Contains("OverridesDefaultStyle", shared, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}\"", shared, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"DG.ToggleButton\"", shared, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource DG.RunSweep}\"", shared, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource DG.SelectionIndicator}\"", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSheetRingUsesTheAccentToEnergySweep()
    {
        var sheet = ReadRepoFile("src/SeedBomb.Wpf/Views/Controls/RunSheet.xaml");
        Assert.Contains("Foreground=\"{DynamicResource DG.RunSweep}\"", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"{DynamicResource DG.Accent}\"", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellPaintsCanvasOnRootGridWithBackdropOff()
    {
        var window = ReadRepoFile("src/SeedBomb.Wpf/Views/Windows/MainWindow.xaml");
        var manager = ReadRepoFile("src/SeedBomb.Wpf/Services/Theme/DesignThemeManager.cs");

        // WPF-UI assigns Window.Background a local fallback brush on startup and on every theme
        // switch, which drops a DynamicResource there; the canvas must live on the root Grid.
        var start = window.IndexOf("<ui:FluentWindow", StringComparison.Ordinal);
        var windowTag = window[start..(window.IndexOf('>', start) + 1)];
        Assert.Contains("WindowBackdropType=\"None\"", windowTag, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=", windowTag, StringComparison.Ordinal);
        Assert.Contains("<Grid Background=\"{DynamicResource DG.Bg}\">", window, StringComparison.Ordinal);
        Assert.Contains("WindowBackdropType.None", manager, StringComparison.Ordinal);
    }

    // WPF-UI's theme dictionaries build these from Color keys via StaticResource, so the
    // TextFillColor*/Accent* Brush overrides never reach the nav pane; each key needs its own Set.
    [Fact]
    public void ApplyPublishesNavPaneBrushesFromPaletteTokens()
    {
        var manager = ReadRepoFile("src/SeedBomb.Wpf/Services/Theme/DesignThemeManager.cs");
        string[] expected =
        [
            "Set(\"NavigationViewItemForeground\", p.TextNav);",
            "Set(\"NavigationViewItemForegroundPointerOver\", p.Text1);",
            "Set(\"NavigationViewItemForegroundPressed\", p.Text2);",
            "Set(\"NavigationViewItemBackgroundPointerOver\", p.ControlHover);",
            "Set(\"NavigationViewItemBackgroundSelected\", p.ItemSelected);",
            "Set(\"NavigationViewItemBackgroundPressed\", p.ControlHover);",
            "Set(\"NavigationViewSelectionIndicatorForeground\", p.SelectionIndicator);",
            "Set(\"NavigationViewItemSeparatorForeground\", p.Divider);",
            "Set(\"LeftNavigationViewSeparatorBrush\", p.Divider);",
        ];
        foreach (var line in expected)
            Assert.Contains(line, manager, StringComparison.Ordinal);
    }

    [Fact]
    public void HighContrastMapsEveryTokenToSystemColors()
    {
        // WR-017: DG.* brushes are bound explicitly, so a WPF-UI theme switch alone left brand
        // colors on screen under High Contrast.
        var resources = new ResourceDictionary();
        DesignThemeManager.PublishTokens(resources, isDark: true, "kiln", highContrast: false);
        Color Solid(object key) => ((SolidColorBrush)resources[key]).Color;
        var ordinaryBg = Solid("DG.Bg");

        DesignThemeManager.PublishTokens(resources, isDark: true, "kiln", highContrast: true);

        var window = SystemColors.WindowColor;
        Color[] system =
        [
            window, SystemColors.WindowTextColor, SystemColors.HighlightColor,
            SystemColors.HighlightTextColor, SystemColors.GrayTextColor, SystemColors.HotTrackColor,
            Color.FromArgb(0xCC, window.R, window.G, window.B),
        ];
        Assert.All(
            resources.Keys.Cast<object>().Where(k => resources[k] is SolidColorBrush),
            k => Assert.Contains(Solid(k), system));
        Assert.Equal(window, Solid("DG.Bg"));
        Assert.Equal(SystemColors.WindowTextColor, Solid("TextFillColorPrimaryBrush"));
        Assert.Equal(SystemColors.GrayTextColor, Solid("TextFillColorDisabledBrush"));
        Assert.Equal(SystemColors.HighlightColor, Solid("KeyboardFocusBorderColorBrush"));
        Assert.Equal(SystemColors.HighlightColor, Solid("NavigationViewSelectionIndicatorForeground"));

        DesignThemeManager.PublishTokens(resources, isDark: true, "kiln", highContrast: false);
        Assert.Equal(ordinaryBg, Solid("DG.Bg"));
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

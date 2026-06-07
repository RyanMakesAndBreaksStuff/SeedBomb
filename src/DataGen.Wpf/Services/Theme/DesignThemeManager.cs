using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace DataGen.Desktop.Services.Theme;

/// <summary>Applies app design tokens alongside WPF-UI theme resources.</summary>
public static class DesignThemeManager
{
    /// <summary>Applies the selected light or dark visual theme.</summary>
    /// <param name="isDark">True to apply dark theme resources; false for light.</param>
    public static void Apply(bool isDark)
    {
        ApplicationThemeManager.Apply(isDark ? ApplicationTheme.Dark : ApplicationTheme.Light);

        var palette = isDark ? Dark : Light;
        Set("DG.Bg", palette.Bg);
        Set("DG.Surface1", palette.Surface1);
        Set("DG.Card", palette.Card);
        Set("DG.Border", palette.Border);
        Set("DG.Text1", palette.Text1);
        Set("DG.Text2", palette.Text2);
        Set("DG.Text3", palette.Text3);

        Set("ApplicationBackgroundBrush", palette.Bg);
        Set("LayerFillColorDefaultBrush", palette.Bg);
        Set("CardBackgroundFillColorDefaultBrush", palette.Card);
        Set("CardBackgroundFillColorSecondaryBrush", palette.Surface1);
        Set("ControlFillColorDefaultBrush", palette.Surface1);
        Set("ControlStrokeColorDefaultBrush", palette.Border);
        Set("DividerStrokeColorDefaultBrush", palette.Border);
        Set("TextFillColorPrimaryBrush", palette.Text1);
        Set("TextFillColorSecondaryBrush", palette.Text2);
        Set("TextFillColorTertiaryBrush", palette.Text3);
    }

    private static void Set(string key, Color color) =>
        Application.Current.Resources[key] = new SolidColorBrush(color);

    private static Color FromRgb(uint rgb) =>
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static readonly Palette Light = new(
        FromRgb(0xF3F0FC),
        FromRgb(0xEDE8FF),
        FromRgb(0xFFFFFF),
        FromRgb(0xDDD5F5),
        FromRgb(0x1A0F3C),
        FromRgb(0x6B5B8A),
        FromRgb(0x9E8EC4));

    private static readonly Palette Dark = new(
        FromRgb(0x0C0A18),
        FromRgb(0x16122A),
        FromRgb(0x1C1834),
        FromRgb(0x38306A),
        FromRgb(0xEDE8FF),
        FromRgb(0x9B8EC4),
        FromRgb(0x5A5080));

    private sealed record Palette(
        Color Bg,
        Color Surface1,
        Color Card,
        Color Border,
        Color Text1,
        Color Text2,
        Color Text3);
}

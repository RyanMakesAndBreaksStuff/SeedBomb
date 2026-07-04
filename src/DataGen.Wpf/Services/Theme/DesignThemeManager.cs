using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Seedbomb.Services.Theme;

/// <summary>A selectable named color palette, offered in each light and dark variant.</summary>
/// <param name="Id">Stable identifier persisted in settings.</param>
/// <param name="DisplayName">Name shown in the palette picker.</param>
/// <param name="LightSwatch">Accent color preview for the light variant.</param>
/// <param name="DarkSwatch">Accent color preview for the dark variant.</param>
public sealed record ThemePaletteOption(string Id, string DisplayName, Brush LightSwatch, Brush DarkSwatch);

/// <summary>Applies app design tokens alongside WPF-UI theme resources.</summary>
public static class DesignThemeManager
{
    /// <summary>Palette ID applied when none is configured.</summary>
    public const string DefaultPaletteId = "slate-steel";

    /// <summary>All palettes available for selection, in display order.</summary>
    // ponytail: property (not a field initializer) so it can read the `Palettes` dictionary
    // declared further down the file without fighting static field init order.
    public static IReadOnlyList<ThemePaletteOption> AvailablePalettes =>
    [
        MakeOption("slate-steel", "Slate / Steel"),
        MakeOption("deep-ocean", "Deep Ocean"),
        MakeOption("ember-forge", "Ember Forge"),
        MakeOption("midnight-orchid", "Dusk Berry"),
        MakeOption("arctic-moss", "Arctic Moss"),
    ];

    private static ThemePaletteOption MakeOption(string id, string displayName)
    {
        var variants = Palettes[id];
        var light = new SolidColorBrush(variants.Light.Accent);
        var dark = new SolidColorBrush(variants.Dark.Accent);
        light.Freeze();
        dark.Freeze();
        return new ThemePaletteOption(id, displayName, light, dark);
    }

    /// <summary>Applies the selected light or dark visual theme using the default palette.</summary>
    /// <param name="isDark">True to apply dark theme resources; false for light.</param>
    public static void Apply(bool isDark) => Apply(isDark, DefaultPaletteId);

    /// <summary>Applies the selected light or dark visual theme for the given palette.</summary>
    /// <param name="isDark">True to apply dark theme resources; false for light.</param>
    /// <param name="paletteId">One of <see cref="AvailablePalettes"/>; unknown IDs fall back to <see cref="DefaultPaletteId"/>.</param>
    public static void Apply(bool isDark, string paletteId)
    {
        ApplicationThemeManager.Apply(isDark ? ApplicationTheme.Dark : ApplicationTheme.Light);

        if (!Palettes.TryGetValue(paletteId, out var variants))
            variants = Palettes[DefaultPaletteId];

        var palette = isDark ? variants.Dark : variants.Light;
        Set("DG.Bg", palette.Bg);
        Set("DG.Surface1", palette.Surface1);
        Set("DG.Surface2", palette.Surface2);
        Set("DG.Card", palette.Card);
        Set("DG.Border", palette.Border);
        Set("DG.Text1", palette.Text1);
        Set("DG.Text2", palette.Text2);
        Set("DG.Text3", palette.Text3);
        Set("DG.Accent", palette.Accent);
        Set("DG.AccentLight", palette.AccentSecondary);
        Set("DG.AccentSoft", palette.AccentSoft);
        Set("DG.Success", palette.Success);
        Set("DG.Warning", palette.Warning);
        Set("DG.Error", palette.Error);
        Set("DG.Info", palette.Info);
        Set("DG.SuccessSoft", palette.SuccessSoft);
        Set("DG.SuccessBorder", palette.SuccessBorder);
        Set("DG.WarningSoft", palette.WarningSoft);
        Set("DG.WarningBorder", palette.WarningBorder);
        Set("DG.ErrorSoft", palette.ErrorSoft);
        Set("DG.ErrorBorder", palette.ErrorBorder);
        Set("DG.InfoSoft", palette.InfoSoft);
        Set("DG.InfoBorder", palette.InfoBorder);

        Set("AccentBrush", palette.Accent);
        Set("ApplicationBackgroundBrush", palette.Bg);
        Set("LayerFillColorDefaultBrush", palette.Bg);
        Set("CardBackgroundFillColorDefaultBrush", palette.Card);
        Set("CardBackgroundFillColorSecondaryBrush", palette.Surface1);
        Set("ControlFillColorDefaultBrush", palette.Surface1);
        Set("ControlStrokeColorDefaultBrush", palette.Border);
        Set("DividerStrokeColorDefaultBrush", palette.Border);
        Set("NavigationViewContentBackground", palette.Bg);
        Set("NavigationViewContentGridBorderBrush", palette.Border);
        Set("TextFillColorPrimaryBrush", palette.Text1);
        Set("TextFillColorSecondaryBrush", palette.Text2);
        Set("TextFillColorTertiaryBrush", palette.Text3);
        Set("AccentFillColorDefaultBrush", palette.Accent);
        Set("AccentFillColorSecondaryBrush", palette.AccentSecondary);
        Set("AccentTextFillColorPrimaryBrush", palette.Accent);
        Set("SystemAccentColorBrush", palette.Accent);
        Set("SystemAccentColor3Brush", palette.AccentSoft);
        Set("SystemFillColorSuccessBackgroundBrush", palette.SuccessSoft);
        Set("SystemFillColorCriticalBackgroundBrush", palette.ErrorSoft);
        Set("SystemFillColorCautionBackgroundBrush", palette.WarningSoft);
    }

    private static void Set(string key, Color color) =>
        Application.Current.Resources[key] = new SolidColorBrush(color);

    private static Color FromRgb(uint rgb) =>
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color FromArgb(byte alpha, uint rgb) =>
        Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ponytail: the source palette collection (Assets/updated__*_mode_palette_collection*.html)
    // only defines background/surface/elevated/text/border/accent per palette — no tertiary text
    // or secondary-surface tone. Text3 is derived by blending Text2 toward Border; Surface2 mirrors
    // the existing Slate/Steel convention (== Surface1 in light, == Card in dark). Both formulas were
    // fit to reproduce the pre-existing hand-picked Slate/Steel Text3 values, then reused for the
    // other four palettes for consistency.
    private static Color Blend(Color from, Color to, double t) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));

    private static (Palette Light, Palette Dark) BuildVariants(
        uint lightBg, uint lightSurface1, uint lightCard, uint lightText1, uint lightText2, uint lightBorder,
        uint lightAccent, uint lightAccentSecondary, uint lightAccentSoft,
        uint darkBg, uint darkSurface1, uint darkCard, uint darkText1, uint darkText2, uint darkBorder,
        uint darkAccent, uint darkAccentSecondary, uint darkAccentSoft)
    {
        var lightText2Color = FromRgb(lightText2);
        var lightBorderColor = FromRgb(lightBorder);
        var darkText2Color = FromRgb(darkText2);
        var darkBorderColor = FromRgb(darkBorder);

        var light = new Palette(
            Bg: FromRgb(lightBg),
            Surface1: FromRgb(lightSurface1),
            Surface2: FromRgb(lightSurface1),
            Card: FromRgb(lightCard),
            Border: lightBorderColor,
            Text1: FromRgb(lightText1),
            Text2: lightText2Color,
            Text3: Blend(lightText2Color, lightBorderColor, 0.30),
            Accent: FromRgb(lightAccent),
            AccentSecondary: FromRgb(lightAccentSecondary),
            AccentSoft: FromRgb(lightAccentSoft),
            Success: StatusLight.Success, Warning: StatusLight.Warning, Error: StatusLight.Error, Info: StatusLight.Info,
            SuccessSoft: StatusLight.SuccessSoft, SuccessBorder: StatusLight.SuccessBorder,
            WarningSoft: StatusLight.WarningSoft, WarningBorder: StatusLight.WarningBorder,
            ErrorSoft: StatusLight.ErrorSoft, ErrorBorder: StatusLight.ErrorBorder,
            InfoSoft: StatusLight.InfoSoft, InfoBorder: StatusLight.InfoBorder);

        var dark = new Palette(
            Bg: FromRgb(darkBg),
            Surface1: FromRgb(darkSurface1),
            Surface2: FromRgb(darkCard),
            Card: FromRgb(darkCard),
            Border: darkBorderColor,
            Text1: FromRgb(darkText1),
            Text2: darkText2Color,
            Text3: Blend(darkText2Color, darkBorderColor, 0.40),
            Accent: FromRgb(darkAccent),
            AccentSecondary: FromRgb(darkAccentSecondary),
            AccentSoft: FromRgb(darkAccentSoft),
            Success: StatusDark.Success, Warning: StatusDark.Warning, Error: StatusDark.Error, Info: StatusDark.Info,
            SuccessSoft: StatusDark.SuccessSoft, SuccessBorder: StatusDark.SuccessBorder,
            WarningSoft: StatusDark.WarningSoft, WarningBorder: StatusDark.WarningBorder,
            ErrorSoft: StatusDark.ErrorSoft, ErrorBorder: StatusDark.ErrorBorder,
            InfoSoft: StatusDark.InfoSoft, InfoBorder: StatusDark.InfoBorder);

        return (light, dark);
    }

    // Status colors are shared across every palette (only light/dark varies), matching the
    // source palette collection where all STATUS entries are identical per mode.
    private static readonly (Color Success, Color Warning, Color Error, Color Info,
        Color SuccessSoft, Color SuccessBorder, Color WarningSoft, Color WarningBorder,
        Color ErrorSoft, Color ErrorBorder, Color InfoSoft, Color InfoBorder) StatusLight = (
        Success: FromRgb(0x2F7D52), Warning: FromRgb(0xA87524), Error: FromRgb(0xB84A4A), Info: FromRgb(0x3E75A8),
        SuccessSoft: FromArgb(0x1A, 0x2F7D52), SuccessBorder: FromArgb(0x40, 0x2F7D52),
        WarningSoft: FromArgb(0x1A, 0xA87524), WarningBorder: FromArgb(0x40, 0xA87524),
        ErrorSoft: FromArgb(0x1A, 0xB84A4A), ErrorBorder: FromArgb(0x40, 0xB84A4A),
        InfoSoft: FromArgb(0x1A, 0x3E75A8), InfoBorder: FromArgb(0x40, 0x3E75A8));

    private static readonly (Color Success, Color Warning, Color Error, Color Info,
        Color SuccessSoft, Color SuccessBorder, Color WarningSoft, Color WarningBorder,
        Color ErrorSoft, Color ErrorBorder, Color InfoSoft, Color InfoBorder) StatusDark = (
        Success: FromRgb(0x68B487), Warning: FromRgb(0xD3A451), Error: FromRgb(0xDF7772), Info: FromRgb(0x78A6D8),
        SuccessSoft: FromArgb(0x26, 0x68B487), SuccessBorder: FromArgb(0x59, 0x68B487),
        WarningSoft: FromArgb(0x26, 0xD3A451), WarningBorder: FromArgb(0x59, 0xD3A451),
        ErrorSoft: FromArgb(0x26, 0xDF7772), ErrorBorder: FromArgb(0x59, 0xDF7772),
        InfoSoft: FromArgb(0x26, 0x78A6D8), InfoBorder: FromArgb(0x59, 0x78A6D8));

    // Base/accent hex values sourced from Assets/updated__light_mode_palette_collection_vol1.html
    // and Assets/updated__dark_mode_palette_collection.html — one entry per palette ID.
    private static readonly IReadOnlyDictionary<string, (Palette Light, Palette Dark)> Palettes =
        new Dictionary<string, (Palette Light, Palette Dark)>
        {
            ["deep-ocean"] = BuildVariants(
                lightBg: 0xF4F8FA, lightSurface1: 0xEAF1F4, lightCard: 0xFFFFFF, lightText1: 0x102027, lightText2: 0x526C75, lightBorder: 0xC9D9DE,
                lightAccent: 0x247C8A, lightAccentSecondary: 0x4DA2AA, lightAccentSoft: 0xD7EEF2,
                darkBg: 0x0B1418, darkSurface1: 0x111E24, darkCard: 0x182A31, darkText1: 0xDCEAEC, darkText2: 0x89A9B1, darkBorder: 0x25404A,
                darkAccent: 0x5BB7C4, darkAccentSecondary: 0x7BCAD3, darkAccentSoft: 0x193B43),

            ["slate-steel"] = BuildVariants(
                lightBg: 0xF5F6F8, lightSurface1: 0xECEFF3, lightCard: 0xFFFFFF, lightText1: 0x151A21, lightText2: 0x606B79, lightBorder: 0xD3D9E1,
                lightAccent: 0x3E6FA8, lightAccentSecondary: 0x668DBE, lightAccentSoft: 0xDCE8F5,
                darkBg: 0x101318, darkSurface1: 0x171C23, darkCard: 0x202733, darkText1: 0xE2E7EE, darkText2: 0x9AA6B5, darkBorder: 0x303A49,
                darkAccent: 0x78A6D8, darkAccentSecondary: 0x96B9E0, darkAccentSoft: 0x21354B),

            ["ember-forge"] = BuildVariants(
                lightBg: 0xFAF5EF, lightSurface1: 0xF1E7DB, lightCard: 0xFFFFFF, lightText1: 0x241912, lightText2: 0x746154, lightBorder: 0xDED0C2,
                lightAccent: 0xA65F3D, lightAccentSecondary: 0xC07855, lightAccentSoft: 0xEEDAC9,
                darkBg: 0x18110D, darkSurface1: 0x231812, darkCard: 0x302219, darkText1: 0xEFE2D7, darkText2: 0xB49B8A, darkBorder: 0x473229,
                darkAccent: 0xD18A63, darkAccentSecondary: 0xE0A07B, darkAccentSoft: 0x4C2F21),

            ["midnight-orchid"] = BuildVariants(
                lightBg: 0xF8F5F7, lightSurface1: 0xEFE8ED, lightCard: 0xFFFFFF, lightText1: 0x211A20, lightText2: 0x766773, lightBorder: 0xDDD2DA,
                lightAccent: 0x8B5570, lightAccentSecondary: 0xAA738B, lightAccentSoft: 0xEADBE4,
                darkBg: 0x151015, darkSurface1: 0x201820, darkCard: 0x2B202B, darkText1: 0xEDE3EA, darkText2: 0xAE9BA9, darkBorder: 0x403241,
                darkAccent: 0xC489A4, darkAccentSecondary: 0xD6A0B8, darkAccentSoft: 0x44283A),

            ["arctic-moss"] = BuildVariants(
                lightBg: 0xF4F8F5, lightSurface1: 0xE8F0EA, lightCard: 0xFFFFFF, lightText1: 0x132019, lightText2: 0x5C7064, lightBorder: 0xCFDDD3,
                lightAccent: 0x3F7D5A, lightAccentSecondary: 0x609A76, lightAccentSoft: 0xDCECE1,
                darkBg: 0x0E1511, darkSurface1: 0x152019, darkCard: 0x1D2B22, darkText1: 0xDDE9E1, darkText2: 0x95AD9D, darkBorder: 0x2E4436,
                darkAccent: 0x6FB486, darkAccentSecondary: 0x8BC99D, darkAccentSoft: 0x214431),
        };

    private sealed record Palette(
        Color Bg,
        Color Surface1,
        Color Surface2,
        Color Card,
        Color Border,
        Color Text1,
        Color Text2,
        Color Text3,
        Color Accent,
        Color AccentSecondary,
        Color AccentSoft,
        Color Success,
        Color Warning,
        Color Error,
        Color Info,
        Color SuccessSoft,
        Color SuccessBorder,
        Color WarningSoft,
        Color WarningBorder,
        Color ErrorSoft,
        Color ErrorBorder,
        Color InfoSoft,
        Color InfoBorder);
}

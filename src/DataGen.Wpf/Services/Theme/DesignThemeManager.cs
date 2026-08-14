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

/// <summary>
/// Applies app design tokens alongside WPF-UI theme resources.
///
/// Four palettes, each light + dark. Every palette declares only ten base values per mode
/// (bg, surface, card, code, two text tones, border, and three accent tones); every other
/// DG.* token is derived here so the palettes cannot drift apart structurally. Add a token by
/// deriving it in <see cref="BuildPalette"/> and publishing it in <see cref="Apply(bool, string)"/> —
/// never by hardcoding a color in a view.
/// </summary>
public static class DesignThemeManager
{
    /// <summary>Palette ID applied when none is configured. Matches the design of record.</summary>
    public const string DefaultPaletteId = "violet-ink";

    /// <summary>When true, chrome animations use zero duration.</summary>
    public static bool ReduceMotion { get; set; }

    /// <summary>Drawer slide duration honoring <see cref="ReduceMotion"/>.</summary>
    public static TimeSpan DrawerAnimationDuration =>
        ReduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250);

    /// <summary>Maps a persisted palette id onto the current four-palette set.</summary>
    public static string ResolvePaletteId(string? paletteId) => paletteId switch
    {
        "slate-steel" => "graphite",
        "deep-ocean" => "tidewater",
        "ember-forge" => "terracotta",
        "midnight-orchid" => "violet-ink",
        "arctic-moss" => "tidewater",
        "violet-ink" or "graphite" or "tidewater" or "terracotta" => paletteId,
        _ => DefaultPaletteId,
    };

    /// <summary>All palettes available for selection, in display order.</summary>
    public static IReadOnlyList<ThemePaletteOption> AvailablePalettes =>
    [
        MakeOption("violet-ink", "Violet Ink"),
        MakeOption("graphite", "Graphite"),
        MakeOption("tidewater", "Tidewater"),
        MakeOption("terracotta", "Terracotta"),
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
    public static void Apply(bool isDark) => Apply(isDark, DefaultPaletteId);

    /// <summary>Applies the selected light or dark visual theme for the given palette.</summary>
    /// <param name="isDark">True to apply dark theme resources; false for light.</param>
    /// <param name="paletteId">One of <see cref="AvailablePalettes"/>; unknown IDs fall back to <see cref="DefaultPaletteId"/>.</param>
    public static void Apply(bool isDark, string paletteId)
    {
        paletteId = ResolvePaletteId(paletteId);
        if (Application.Current is null)
            return;

        ApplicationThemeManager.Apply(isDark ? ApplicationTheme.Dark : ApplicationTheme.Light);

        var variants = Palettes[paletteId];
        var p = isDark ? variants.Dark : variants.Light;

        // --- Surfaces ---------------------------------------------------
        Set("DG.Bg", p.Bg);
        Set("DG.TitleBar", p.TitleBar);
        Set("DG.Surface1", p.Surface1);
        Set("DG.Surface2", p.Surface2);
        Set("DG.Card", p.Card);
        Set("DG.CardSunken", p.CardSunken);
        Set("DG.CodeSurface", p.CodeSurface);

        // --- Lines ------------------------------------------------------
        Set("DG.Border", p.Border);
        Set("DG.BorderStrong", p.BorderStrong);
        Set("DG.Divider", p.Divider);

        // --- Controls ---------------------------------------------------
        Set("DG.ControlFill", p.ControlFill);
        Set("DG.ControlBorder", p.ControlBorder);
        Set("DG.ControlHover", p.ControlHover);
        Set("DG.ItemSelected", p.ItemSelected);
        Set("DG.RowSelected", p.RowSelected);
        Set("DG.TrackFill", p.TrackFill);
        Set("DG.Scrim", p.Scrim);

        // --- Text -------------------------------------------------------
        Set("DG.Text1", p.Text1);
        Set("DG.TextBody", p.TextBody);
        Set("DG.TextNav", p.TextNav);
        Set("DG.Text2", p.Text2);
        Set("DG.Text3", p.Text3);
        Set("DG.TextDisabled", p.TextDisabled);

        // --- Accent -----------------------------------------------------
        Set("DG.Accent", p.Accent);
        Set("DG.AccentHover", p.AccentHover);
        Set("DG.AccentLight", p.AccentHover);
        Set("DG.AccentPressed", p.AccentPressed);
        Set("DG.OnAccent", p.OnAccent);
        Set("DG.AccentText", p.AccentText);
        Set("DG.AccentSoft", p.AccentSoft);
        Set("DG.AccentSoftBorder", p.AccentSoftBorder);
        Set("DG.AccentChip", p.AccentChip);
        Set("DG.AccentChipBorder", p.AccentChipBorder);

        // --- Status -----------------------------------------------------
        Set("DG.Success", p.Success);
        Set("DG.SuccessSoft", p.SuccessSoft);
        Set("DG.SuccessBorder", p.SuccessBorder);
        Set("DG.Warning", p.Warning);
        Set("DG.WarningSoft", p.WarningSoft);
        Set("DG.WarningBorder", p.WarningBorder);
        Set("DG.WarningText", p.WarningText);
        Set("DG.Error", p.Error);
        Set("DG.ErrorSoft", p.ErrorSoft);
        Set("DG.ErrorBorder", p.ErrorBorder);
        Set("DG.Info", p.Info);
        Set("DG.InfoSoft", p.InfoSoft);
        Set("DG.InfoBorder", p.InfoBorder);

        // --- Legacy key retained for existing references -----------------
        Set("AccentBrush", p.Accent);

        // --- WPF-UI chrome ----------------------------------------------
        Set("ApplicationBackgroundBrush", p.Bg);
        Set("LayerFillColorDefaultBrush", p.Surface1);
        Set("CardBackgroundFillColorDefaultBrush", p.Card);
        Set("CardBackgroundFillColorSecondaryBrush", p.CardSunken);
        Set("ControlFillColorDefaultBrush", p.ControlFill);
        Set("ControlStrokeColorDefaultBrush", p.ControlBorder);
        Set("DividerStrokeColorDefaultBrush", p.Border);
        Set("NavigationViewContentBackground", p.Surface1);
        Set("NavigationViewContentGridBorderBrush", p.Border);
        Set("TextFillColorPrimaryBrush", p.Text1);
        Set("TextFillColorSecondaryBrush", p.Text2);
        Set("TextFillColorTertiaryBrush", p.Text3);
        Set("TextFillColorDisabledBrush", p.TextDisabled);
        Set("AccentFillColorDefaultBrush", p.Accent);
        Set("AccentFillColorSecondaryBrush", p.AccentHover);
        Set("AccentFillColorTertiaryBrush", p.AccentPressed);
        Set("AccentTextFillColorPrimaryBrush", p.AccentText);
        Set("TextOnAccentFillColorPrimaryBrush", p.OnAccent);
        Set("SystemAccentColorBrush", p.Accent);
        Set("SystemAccentColorPrimaryBrush", p.Accent);
        Set("SystemAccentColor3Brush", p.AccentChip);
        Set("SystemFillColorSuccessBrush", p.Success);
        Set("SystemFillColorCautionBrush", p.Warning);
        Set("SystemFillColorCriticalBrush", p.Error);
        Set("SystemFillColorSuccessBackgroundBrush", p.SuccessSoft);
        Set("SystemFillColorCautionBackgroundBrush", p.WarningSoft);
        Set("SystemFillColorCriticalBackgroundBrush", p.ErrorSoft);
    }

    private static void Set(string key, Color color)
    {
        if (Application.Current is not { } app)
            return;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    private static Color FromRgb(uint rgb) =>
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color WithAlpha(byte alpha, Color c) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Blend(Color from, Color to, double t) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));

    /// <summary>
    /// Expands ten declared base values into the full token set. Derivation direction flips with
    /// <paramref name="isDark"/>: dark modes lift toward white, light modes settle toward black.
    /// </summary>
    private static Palette BuildPalette(
        bool isDark,
        uint bg, uint titleBar, uint surface1, uint card, uint codeSurface,
        uint text1, uint text2, uint border,
        uint accent, uint accentHover, uint accentPressed, uint onAccent)
    {
        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        var black = Color.FromRgb(0x00, 0x00, 0x00);

        var bgC = FromRgb(bg);
        var cardC = FromRgb(card);
        var borderC = FromRgb(border);
        var text1C = FromRgb(text1);
        var text2C = FromRgb(text2);
        var accentC = FromRgb(accent);

        // In dark mode controls sit above the card; in light mode they sit just below it.
        var lift = isDark ? white : black;
        var controlFill = Blend(cardC, lift, isDark ? 0.04 : 0.015);
        var controlHover = Blend(cardC, lift, isDark ? 0.07 : 0.035);

        var status = isDark ? StatusDark : StatusLight;
        var softAlpha = isDark ? (byte)0x26 : (byte)0x1A;
        var borderAlpha = isDark ? (byte)0x59 : (byte)0x40;

        return new Palette(
            Bg: bgC,
            TitleBar: FromRgb(titleBar),
            Surface1: FromRgb(surface1),
            Surface2: Blend(cardC, bgC, 0.55),
            Card: cardC,
            CardSunken: Blend(cardC, bgC, isDark ? 0.75 : 0.45),
            CodeSurface: FromRgb(codeSurface),

            Border: borderC,
            BorderStrong: Blend(borderC, lift, isDark ? 0.14 : 0.10),
            Divider: Blend(borderC, cardC, 0.55),

            ControlFill: controlFill,
            ControlBorder: Blend(borderC, lift, isDark ? 0.05 : 0.02),
            ControlHover: controlHover,
            ItemSelected: Blend(bgC, lift, isDark ? 0.07 : 0.045),
            RowSelected: isDark ? Blend(cardC, accentC, 0.14) : Blend(cardC, accentC, 0.10),
            TrackFill: Blend(cardC, lift, isDark ? 0.08 : 0.06),
            Scrim: WithAlpha(isDark ? (byte)0x9E : (byte)0x66, Blend(bgC, black, 0.6)),

            Text1: text1C,
            TextBody: Blend(text1C, text2C, 0.22),
            TextNav: Blend(text1C, text2C, 0.30),
            Text2: text2C,
            Text3: Blend(text2C, borderC, isDark ? 0.40 : 0.30),
            TextDisabled: Blend(text2C, borderC, isDark ? 0.68 : 0.55),

            Accent: accentC,
            AccentHover: FromRgb(accentHover),
            AccentPressed: FromRgb(accentPressed),
            OnAccent: FromRgb(onAccent),
            // Accent text must read against Card, so it moves away from the accent, not toward it.
            AccentText: isDark ? Blend(accentC, white, 0.35) : Blend(accentC, black, 0.18),
            AccentSoft: Blend(cardC, accentC, isDark ? 0.16 : 0.09),
            AccentSoftBorder: Blend(cardC, accentC, isDark ? 0.42 : 0.30),
            AccentChip: Blend(cardC, accentC, isDark ? 0.24 : 0.14),
            AccentChipBorder: Blend(cardC, accentC, isDark ? 0.50 : 0.34),

            Success: status.Success,
            SuccessSoft: Blend(cardC, status.Success, isDark ? 0.16 : 0.10),
            SuccessBorder: WithAlpha(borderAlpha, status.Success),
            Warning: status.Warning,
            WarningSoft: Blend(cardC, status.Warning, isDark ? 0.18 : 0.12),
            WarningBorder: WithAlpha(borderAlpha, status.Warning),
            WarningText: isDark ? Blend(status.Warning, white, 0.55) : Blend(status.Warning, black, 0.30),
            Error: status.Error,
            ErrorSoft: Blend(cardC, status.Error, isDark ? 0.16 : 0.10),
            ErrorBorder: WithAlpha(borderAlpha, status.Error),
            Info: status.Info,
            InfoSoft: WithAlpha(softAlpha, status.Info),
            InfoBorder: WithAlpha(borderAlpha, status.Info));
    }

    // Status hues are shared across all four palettes; only the light/dark pair varies. Keeping them
    // palette-independent means "rejected", "done" and "throttled" always read the same way.
    private static readonly StatusSet StatusLight = new(
        Success: FromRgb(0x1F7A4D), Warning: FromRgb(0x8A5A00),
        Error: FromRgb(0xB02020), Info: FromRgb(0x3E75A8));

    private static readonly StatusSet StatusDark = new(
        Success: FromRgb(0x5BB98B), Warning: FromRgb(0xE0A030),
        Error: FromRgb(0xE08A8A), Info: FromRgb(0x8FB6E2));

    private static readonly IReadOnlyDictionary<string, (Palette Light, Palette Dark)> Palettes =
        new Dictionary<string, (Palette Light, Palette Dark)>
        {
            // Design of record. Cool violet on true Win11 neutrals — the accent carries the
            // personality, every surface stays achromatic so dense tables stay legible.
            ["violet-ink"] = (
                BuildPalette(false,
                    bg: 0xF3F3F3, titleBar: 0xF9F9F9, surface1: 0xFAFAFA, card: 0xFFFFFF, codeSurface: 0xF4F4F6,
                    text1: 0x151A21, text2: 0x5C6470, border: 0xE1E1E1,
                    accent: 0x7160E8, accentHover: 0x8574EE, accentPressed: 0x5B4ACF, onAccent: 0xFFFFFF),
                BuildPalette(true,
                    bg: 0x202020, titleBar: 0x1B1B1B, surface1: 0x272727, card: 0x2B2B2B, codeSurface: 0x1E1E1E,
                    text1: 0xFFFFFF, text2: 0xA0A0A0, border: 0x383838,
                    accent: 0xA79CF1, accentHover: 0xB8AFF5, accentPressed: 0x8F82E4, onAccent: 0x1B1338)),

            // Fully achromatic with a steel-blue accent. The quiet option for long sessions.
            ["graphite"] = (
                BuildPalette(false,
                    bg: 0xF4F5F6, titleBar: 0xFAFAFB, surface1: 0xF8F9FA, card: 0xFFFFFF, codeSurface: 0xF3F4F6,
                    text1: 0x14181D, text2: 0x5A626C, border: 0xDFE1E4,
                    accent: 0x4B6B8A, accentHover: 0x5C7E9E, accentPressed: 0x3B5772, onAccent: 0xFFFFFF),
                BuildPalette(true,
                    bg: 0x1C1E21, titleBar: 0x181A1C, surface1: 0x232629, card: 0x282B2F, codeSurface: 0x191B1E,
                    text1: 0xF2F4F6, text2: 0x9BA3AC, border: 0x353A3F,
                    accent: 0x8FB3D4, accentHover: 0xA3C2DE, accentPressed: 0x789EC2, onAccent: 0x101C26)),

            // Desaturated blue-green. Reads as "tooling" without going neon.
            ["tidewater"] = (
                BuildPalette(false,
                    bg: 0xF2F6F6, titleBar: 0xF9FBFB, surface1: 0xF7FAFA, card: 0xFFFFFF, codeSurface: 0xF0F5F5,
                    text1: 0x0F1D1E, text2: 0x536566, border: 0xD9E3E3,
                    accent: 0x1E7A78, accentHover: 0x2A8F8C, accentPressed: 0x16615F, onAccent: 0xFFFFFF),
                BuildPalette(true,
                    bg: 0x191F1F, titleBar: 0x151A1A, surface1: 0x1F2726, card: 0x242D2C, codeSurface: 0x161C1C,
                    text1: 0xEFF5F4, text2: 0x94A8A7, border: 0x323D3C,
                    accent: 0x6FC2BD, accentHover: 0x86D0CB, accentPressed: 0x55A8A3, onAccent: 0x08211F)),

            // Warm counterweight to the other three. Clay accent on a faintly warm neutral.
            ["terracotta"] = (
                BuildPalette(false,
                    bg: 0xF7F4F1, titleBar: 0xFBF9F8, surface1: 0xFAF8F6, card: 0xFFFFFF, codeSurface: 0xF5F1EE,
                    text1: 0x1E1815, text2: 0x6B605A, border: 0xE4DDD7,
                    accent: 0xA9542F, accentHover: 0xBC6440, accentPressed: 0x8C4324, onAccent: 0xFFFFFF),
                BuildPalette(true,
                    bg: 0x221D1A, titleBar: 0x1D1916, surface1: 0x292320, card: 0x2E2723, codeSurface: 0x1E1A17,
                    text1: 0xF6F0EC, text2: 0xAB9C93, border: 0x3D352F,
                    accent: 0xDC9068, accentHover: 0xE6A47F, accentPressed: 0xC5764D, onAccent: 0x2A1306)),
        };

    private readonly record struct StatusSet(Color Success, Color Warning, Color Error, Color Info);

    private sealed record Palette(
        Color Bg,
        Color TitleBar,
        Color Surface1,
        Color Surface2,
        Color Card,
        Color CardSunken,
        Color CodeSurface,
        Color Border,
        Color BorderStrong,
        Color Divider,
        Color ControlFill,
        Color ControlBorder,
        Color ControlHover,
        Color ItemSelected,
        Color RowSelected,
        Color TrackFill,
        Color Scrim,
        Color Text1,
        Color TextBody,
        Color TextNav,
        Color Text2,
        Color Text3,
        Color TextDisabled,
        Color Accent,
        Color AccentHover,
        Color AccentPressed,
        Color OnAccent,
        Color AccentText,
        Color AccentSoft,
        Color AccentSoftBorder,
        Color AccentChip,
        Color AccentChipBorder,
        Color Success,
        Color SuccessSoft,
        Color SuccessBorder,
        Color Warning,
        Color WarningSoft,
        Color WarningBorder,
        Color WarningText,
        Color Error,
        Color ErrorSoft,
        Color ErrorBorder,
        Color Info,
        Color InfoSoft,
        Color InfoBorder);
}

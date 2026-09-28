using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace SeedBomb.Services.Theme;

/// <summary>A selectable named color palette, offered in each light and dark variant.</summary>
/// <param name="Id">Stable identifier persisted in settings.</param>
/// <param name="DisplayName">Name shown in the palette picker.</param>
/// <param name="LightSwatch">Accent color preview for the light variant.</param>
/// <param name="DarkSwatch">Accent color preview for the dark variant.</param>
public sealed record ThemePaletteOption(string Id, string DisplayName, Brush LightSwatch, Brush DarkSwatch);

/// <summary>
/// Applies app design tokens alongside WPF-UI theme resources.
///
/// Two palettes (Kiln, Notes), each light + dark. Every palette declares the sheet values;
/// <see cref="BuildPalette"/> derives only the tokens no sheet defines. Add a token by
/// deriving it in <see cref="BuildPalette"/> and publishing it in <see cref="Apply(bool, string)"/> —
/// never by hardcoding a color in a view.
/// </summary>
public static class DesignThemeManager
{
    /// <summary>Palette ID applied when none is configured. Matches the design of record.</summary>
    public const string DefaultPaletteId = "kiln";

    /// <summary>Maps a persisted palette id onto the current palette set.</summary>
    public static string ResolvePaletteId(string? paletteId) => paletteId switch
    {
        // Every palette that predates the Kiln/Notes sheets lands on the default. Guessing a
        // nearest-hue match would be precision the sheets do not support.
        "kiln" or "notes" => paletteId,
        _ => DefaultPaletteId,
    };

    /// <summary>All palettes available for selection, in display order.</summary>
    public static IReadOnlyList<ThemePaletteOption> AvailablePalettes =>
    [
        MakeOption("kiln", "Kiln"),
        MakeOption("notes", "Notes"),
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

    // Last Apply arguments, replayed when Windows High Contrast is toggled.
    private static bool _highContrastSubscribed;
    private static bool _lastIsDark;
    private static string _lastPaletteId = DefaultPaletteId;

    /// <summary>Applies the selected light or dark visual theme for the given palette.</summary>
    /// <param name="isDark">True to apply dark theme resources; false for light.</param>
    /// <param name="paletteId">One of <see cref="AvailablePalettes"/>; unknown IDs fall back to <see cref="DefaultPaletteId"/>.</param>
    public static void Apply(bool isDark, string paletteId)
    {
        paletteId = ResolvePaletteId(paletteId);
        _lastIsDark = isDark;
        _lastPaletteId = paletteId;
        EnsureHighContrastSubscription();
        // Test host may have a leftover Application from an STA fixture; production always calls this on the UI thread.
        if (Application.Current is not { } app || !app.Dispatcher.CheckAccess())
            return;

        SetImage("DG.Logo", isDark
            ? "pack://application:,,,/Resources/logo-dark.png"
            : "pack://application:,,,/Resources/logo-light.png");

        var highContrast = SystemParameters.HighContrast;
        // None matches MainWindow. The default (Mica) re-enables the DWM backdrop and clears the
        // window background on every switch, under an opaque palette that would hide it anyway.
        ApplicationThemeManager.Apply(
            highContrast ? ApplicationTheme.HighContrast : isDark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.None);
        PublishTokens(app.Resources, isDark, paletteId, highContrast);
    }

    /// <summary>
    /// Writes every design token and WPF-UI override into <paramref name="r"/>. Under High Contrast
    /// each one maps to a Windows system color (WR-017), overwriting any palette value written before.
    /// </summary>
    internal static void PublishTokens(ResourceDictionary r, bool isDark, string paletteId, bool highContrast)
    {
        var variants = Palettes[ResolvePaletteId(paletteId)];
        var p = highContrast ? HighContrastPalette() : isDark ? variants.Dark : variants.Light;

        // --- Surfaces ---------------------------------------------------
        r.Set("DG.Bg", p.Bg);
        r.Set("DG.TitleBar", p.TitleBar);
        r.Set("DG.Surface1", p.Surface1);
        r.Set("DG.Surface2", p.Surface2);
        r.Set("DG.Card", p.Card);
        r.Set("DG.CardSunken", p.CardSunken);
        r.Set("DG.CodeSurface", p.CodeSurface);

        // --- Lines ------------------------------------------------------
        r.Set("DG.Border", p.Border);
        r.Set("DG.BorderStrong", p.BorderStrong);
        r.Set("DG.Divider", p.Divider);
        r.Set("DG.Focus", p.Focus);

        // --- Controls ---------------------------------------------------
        r.Set("DG.ControlFill", p.ControlFill);
        r.Set("DG.ControlBorder", p.ControlBorder);
        r.Set("DG.ControlHover", p.ControlHover);
        r.Set("DG.ItemSelected", p.ItemSelected);
        r.Set("DG.RowSelected", p.RowSelected);
        r.Set("DG.TrackFill", p.TrackFill);
        r.Set("DG.Scrim", p.Scrim);

        // --- Text -------------------------------------------------------
        r.Set("DG.Text1", p.Text1);
        r.Set("DG.TextBody", p.TextBody);
        r.Set("DG.TextNav", p.TextNav);
        r.Set("DG.Text2", p.Text2);
        r.Set("DG.Text3", p.Text3);
        r.Set("DG.TextDisabled", p.TextDisabled);

        // --- Accent -----------------------------------------------------
        r.Set("DG.Accent", p.Accent);
        r.Set("DG.AccentHover", p.AccentHover);
        r.Set("DG.AccentLight", p.AccentHover);
        r.Set("DG.AccentPressed", p.AccentPressed);
        r.Set("DG.OnAccent", p.OnAccent);
        r.Set("DG.AccentText", p.AccentText);
        r.Set("DG.AccentSoft", p.AccentSoft);
        r.Set("DG.AccentSoftBorder", p.AccentSoftBorder);
        r.Set("DG.AccentChip", p.AccentChip);
        r.Set("DG.AccentChipBorder", p.AccentChipBorder);
        r.Set("DG.SelectionIndicator", p.SelectionIndicator);

        // --- Status -----------------------------------------------------
        r.Set("DG.Success", p.Success);
        r.Set("DG.SuccessSoft", p.SuccessSoft);
        r.Set("DG.Warning", p.Warning);
        r.Set("DG.WarningSoft", p.WarningSoft);
        r.Set("DG.WarningBorder", p.WarningBorder);
        r.Set("DG.WarningText", p.WarningText);
        r.Set("DG.Error", p.Error);
        r.Set("DG.ErrorSoft", p.ErrorSoft);
        r.Set("DG.ErrorBorder", p.ErrorBorder);
        r.Set("DG.Info", p.Info);
        r.Set("DG.InfoSoft", p.InfoSoft);

        // --- Energy & series --------------------------------------------
        r.Set("DG.Energy", p.Energy);
        r.Set("DG.Series1", p.Series1);
        r.Set("DG.Series2", p.Series2);
        r.Set("DG.Series3", p.Series3);
        r.Set("DG.Series4", p.Series4);
        r.Set("DG.Series5", p.Series5);
        r.SetSweep("DG.RunSweep", p.Accent, p.Energy);

        // --- Legacy key retained for existing references -----------------
        r.Set("AccentBrush", p.Accent);

        // --- WPF-UI chrome ----------------------------------------------
        r.Set("ApplicationBackgroundBrush", p.Bg);
        r.Set("LayerFillColorDefaultBrush", p.Surface1);
        r.Set("CardBackgroundFillColorDefaultBrush", p.Card);
        r.Set("CardBackgroundFillColorSecondaryBrush", p.CardSunken);
        r.Set("ControlFillColorDefaultBrush", p.ControlFill);
        r.Set("ControlStrokeColorDefaultBrush", p.ControlBorder);
        r.Set("KeyboardFocusBorderColorBrush", p.Focus);
        r.Set("DividerStrokeColorDefaultBrush", p.Border);
        r.Set("NavigationViewContentBackground", p.Surface1);
        r.Set("NavigationViewContentGridBorderBrush", p.Border);
        // WPF-UI builds these from Color keys via StaticResource, so the Brush overrides above
        // never reach the nav pane. Background (rest) stays WPF-UI's transparent.
        r.Set("NavigationViewItemForeground", p.TextNav);
        r.Set("NavigationViewItemForegroundPointerOver", p.Text1);
        r.Set("NavigationViewItemForegroundPressed", p.Text2);
        r.Set("NavigationViewItemBackgroundPointerOver", p.ControlHover);
        r.Set("NavigationViewItemBackgroundSelected", p.ItemSelected);
        r.Set("NavigationViewItemBackgroundPressed", p.ControlHover);
        // Otherwise the pill takes SystemAccentColorPrimary, i.e. the Windows accent color.
        r.Set("NavigationViewSelectionIndicatorForeground", p.SelectionIndicator);
        r.Set("NavigationViewItemSeparatorForeground", p.Divider);
        r.Set("LeftNavigationViewSeparatorBrush", p.Divider);
        r.Set("TextFillColorPrimaryBrush", p.Text1);
        r.Set("TextFillColorSecondaryBrush", p.Text2);
        r.Set("TextFillColorTertiaryBrush", p.Text3);
        r.Set("TextFillColorDisabledBrush", p.TextDisabled);
        r.Set("AccentFillColorDefaultBrush", p.Accent);
        r.Set("AccentFillColorSecondaryBrush", p.AccentHover);
        r.Set("AccentFillColorTertiaryBrush", p.AccentPressed);
        r.Set("AccentTextFillColorPrimaryBrush", p.AccentText);
        r.Set("TextOnAccentFillColorPrimaryBrush", p.OnAccent);
        r.Set("SystemAccentColorBrush", p.Accent);
        r.Set("SystemAccentColorPrimaryBrush", p.Accent);
        r.Set("SystemAccentColor3Brush", p.AccentChip);
        r.Set("SystemFillColorSuccessBrush", p.Success);
        r.Set("SystemFillColorCautionBrush", p.Warning);
        r.Set("SystemFillColorCriticalBrush", p.Error);
        r.Set("SystemFillColorSuccessBackgroundBrush", p.SuccessSoft);
        r.Set("SystemFillColorCautionBackgroundBrush", p.WarningSoft);
        r.Set("SystemFillColorCriticalBackgroundBrush", p.ErrorSoft);
    }

    private static void Set(this ResourceDictionary r, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        r[key] = brush;
    }

    private static void SetImage(string key, string packUri)
    {
        if (Application.Current is not { } app)
            return;
        var image = new BitmapImage(new Uri(packUri, UriKind.Absolute));
        image.Freeze();
        app.Resources[key] = image;
    }

    // ponytail: WPF has no conic gradient, so the "run heats up" sweep is a 45° linear
    // Accent → Energy ramp. Upgrade to an ArcSegment ring control if that reads wrong.
    private static void SetSweep(this ResourceDictionary r, string key, Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, 45d);
        brush.Freeze();
        r[key] = brush;
    }

    /// <summary>Re-applies the last palette when Windows High Contrast is toggled at runtime.</summary>
    private static void EnsureHighContrastSubscription()
    {
        if (_highContrastSubscribed) return;
        _highContrastSubscribed = true;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                Apply(_lastIsDark, _lastPaletteId);
        };
    }

    // WR-017: High Contrast gives every token a Windows system color. Status hues collapse to text:
    // contrast themes carry meaning through text and icons, not hue. Selection keeps the window
    // background so text on it stays readable; the indicator and focus take the highlight.
    private static Palette HighContrastPalette()
    {
        var window = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var highlight = SystemColors.HighlightColor;
        return new Palette(
            Bg: window, TitleBar: window, Surface1: window, Surface2: window,
            Card: window, CardSunken: window, CodeSurface: window,
            Border: text, BorderStrong: text, Divider: text, Focus: highlight,
            ControlFill: window, ControlBorder: text, ControlHover: window,
            ItemSelected: window, RowSelected: window, TrackFill: window,
            Scrim: Color.FromArgb(0xCC, window.R, window.G, window.B),
            Text1: text, TextBody: text, TextNav: text, Text2: text, Text3: text,
            TextDisabled: SystemColors.GrayTextColor,
            Accent: highlight, AccentHover: highlight, AccentPressed: highlight,
            OnAccent: SystemColors.HighlightTextColor, AccentText: SystemColors.HotTrackColor,
            AccentSoft: window, AccentSoftBorder: text, AccentChip: window, AccentChipBorder: text,
            SelectionIndicator: highlight,
            Success: text, SuccessSoft: window, Warning: text, WarningSoft: window,
            WarningBorder: text, WarningText: text,
            Error: text, ErrorSoft: window, ErrorBorder: text,
            Info: text, InfoSoft: window,
            Energy: highlight,
            Series1: text, Series2: text, Series3: text, Series4: text, Series5: text);
    }

    private static Color FromRgb(uint rgb) =>
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color WithAlpha(byte alpha, Color c) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Blend(Color from, Color to, double t) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));

    /// <summary>Flattens an #AARRGGBB overlay onto an opaque base, matching the color sheets.</summary>
    private static Color Over(uint overlay, Color baseColor) => Blend(
        baseColor,
        Color.FromRgb((byte)(overlay >> 16), (byte)(overlay >> 8), (byte)overlay),
        (byte)(overlay >> 24) / 255.0);

    private static Color FromArgb(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>
    /// Expands one color sheet's declared values into the full token set. Only the tokens no
    /// sheet defines are derived here; everything else is the sheet value verbatim.
    /// </summary>
    private static Palette BuildPalette(
        bool isDark,
        uint canvasBg, uint pageBg, uint cardBg, uint subtleBg, uint insetBg, uint scrimBg,
        uint dividerStroke, uint controlStroke, uint strongStroke, uint focusStroke,
        uint textPrimary, uint textSecondary, uint textTertiary, uint textDisabled,
        uint textOnAccent, uint linkText,
        uint accentFill, uint accentFillHover, uint accentFillPressed,
        uint accentTint, uint accentTintStroke, uint selectionIndicator,
        uint controlFill, uint controlFillHover, uint hoverOverlay,
        uint successText, uint successTint,
        uint dangerText, uint dangerTint, uint dangerStroke,
        uint warningText, uint warningTint,
        uint energyFill, uint progressTrack,
        uint s1, uint s2, uint s3, uint s4, uint s5)
    {
        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        var black = Color.FromRgb(0x00, 0x00, 0x00);

        var cardC = FromRgb(cardBg);
        var borderC = FromRgb(dividerStroke);
        var text1C = FromRgb(textPrimary);
        var text2C = FromRgb(textSecondary);
        var accentC = FromRgb(accentFill);
        var tintC = FromRgb(accentTint);
        var tintStrokeC = FromRgb(accentTintStroke);
        var warnC = FromRgb(warningText);
        var successC = FromRgb(successText);
        var borderAlpha = isDark ? (byte)0x59 : (byte)0x40;

        return new Palette(
            Bg: FromRgb(canvasBg),
            TitleBar: FromRgb(canvasBg),
            Surface1: FromRgb(pageBg),
            Surface2: FromRgb(subtleBg),
            Card: cardC,
            CardSunken: FromRgb(insetBg),
            CodeSurface: FromRgb(insetBg),
            Border: borderC,
            BorderStrong: FromRgb(strongStroke),
            // Row rules must read softer than card outlines; the sheets ship one stroke value.
            Divider: Blend(borderC, cardC, 0.55),
            Focus: FromRgb(focusStroke),
            ControlFill: FromRgb(controlFill),
            ControlBorder: FromRgb(controlStroke),
            ControlHover: FromRgb(controlFillHover),
            ItemSelected: FromRgb(subtleBg),
            RowSelected: Over(hoverOverlay, cardC),
            TrackFill: FromRgb(progressTrack),
            Scrim: FromArgb(scrimBg),
            Text1: text1C,
            TextBody: Blend(text1C, text2C, 0.22),
            TextNav: Blend(text1C, text2C, 0.30),
            Text2: text2C,
            Text3: FromRgb(textTertiary),
            TextDisabled: FromRgb(textDisabled),
            Accent: accentC,
            AccentHover: FromRgb(accentFillHover),
            AccentPressed: FromRgb(accentFillPressed),
            OnAccent: FromRgb(textOnAccent),
            AccentText: FromRgb(linkText),
            AccentSoft: tintC,
            AccentSoftBorder: tintStrokeC,
            AccentChip: Blend(tintC, accentC, isDark ? 0.24 : 0.14),
            AccentChipBorder: Blend(tintStrokeC, accentC, isDark ? 0.30 : 0.20),
            SelectionIndicator: FromRgb(selectionIndicator),
            Success: successC,
            SuccessSoft: FromRgb(successTint),
            Warning: warnC,
            WarningSoft: FromRgb(warningTint),
            WarningBorder: WithAlpha(borderAlpha, warnC),
            WarningText: isDark ? Blend(warnC, white, 0.55) : Blend(warnC, black, 0.30),
            Error: FromRgb(dangerText),
            ErrorSoft: FromRgb(dangerTint),
            ErrorBorder: FromRgb(dangerStroke),
            // Neither sheet ships an info hue — both draw the info banner in accent tint.
            Info: accentC,
            InfoSoft: tintC,
            Energy: FromRgb(energyFill),
            Series1: FromRgb(s1), Series2: FromRgb(s2), Series3: FromRgb(s3),
            Series4: FromRgb(s4), Series5: FromRgb(s5));
    }

    private static readonly IReadOnlyDictionary<string, (Palette Light, Palette Dark)> Palettes =
        new Dictionary<string, (Palette Light, Palette Dark)>
        {
            // Design of record. Charcoal-and-citron. Two hard rules from the sheet:
            // citron is a fill and never a font on a light surface, and citron never
            // encodes status — Success/Error stay emerald/brick.
            ["kiln"] = (
                BuildPalette(false,
                    canvasBg: 0xF0EEE8, pageBg: 0xFAF9F5, cardBg: 0xFFFFFF, subtleBg: 0xEAE8E0,
                    insetBg: 0xF4F2EB, scrimBg: 0x661C1A16,
                    dividerStroke: 0xE3E0D7, controlStroke: 0xD2CEC3, strongStroke: 0xA9A497,
                    focusStroke: 0x47600A,
                    textPrimary: 0x1C1B18, textSecondary: 0x5C584E, textTertiary: 0x7A7568,
                    textDisabled: 0xA8A297, textOnAccent: 0xFFFFFF, linkText: 0x47600A,
                    accentFill: 0x262420, accentFillHover: 0x3A362E, accentFillPressed: 0x14130F,
                    accentTint: 0xF0F6D9, accentTintStroke: 0xD9E8A3, selectionIndicator: 0x5F7F0B,
                    controlFill: 0xFFFFFF, controlFillHover: 0xF3F1EA, hoverOverlay: 0x141C1A16,
                    successText: 0x1F7A4D, successTint: 0xE4F4EA,
                    dangerText: 0xB3261E, dangerTint: 0xFBEAE7, dangerStroke: 0xEFC0B8,
                    warningText: 0x92610A, warningTint: 0xFBF0D8,
                    energyFill: 0x5F7F0B, progressTrack: 0xE7E4DA,
                    s1: 0x262420, s2: 0x5F7F0B, s3: 0xA85C1E, s4: 0x2B5F7A, s5: 0x7A3D6B),
                BuildPalette(true,
                    canvasBg: 0x121110, pageBg: 0x191816, cardBg: 0x201F1C, subtleBg: 0x2A2825,
                    insetBg: 0x0E0D0C, scrimBg: 0x99000000,
                    dividerStroke: 0x2E2C28, controlStroke: 0x3D3A34, strongStroke: 0x57534A,
                    focusStroke: 0xBADD52,
                    textPrimary: 0xF2F0EA, textSecondary: 0xB4AFA3, textTertiary: 0x8B857A,
                    textDisabled: 0x625D54, textOnAccent: 0x14130F, linkText: 0xC2E060,
                    accentFill: 0xBADD52, accentFillHover: 0xC9E76F, accentFillPressed: 0xA6C93F,
                    accentTint: 0x24290F, accentTintStroke: 0x3E4A18, selectionIndicator: 0xBADD52,
                    controlFill: 0x2A2825, controlFillHover: 0x35322D, hoverOverlay: 0x14FFFFFF,
                    successText: 0x5BD08D, successTint: 0x14251C,
                    dangerText: 0xFF8E80, dangerTint: 0x2E1815, dangerStroke: 0x63302A,
                    warningText: 0xE8B04B, warningTint: 0x2C2313,
                    energyFill: 0x9DCB1F, progressTrack: 0x33302B,
                    s1: 0xD8D3C8, s2: 0xBADD52, s3: 0xE0965A, s4: 0x6FB4D6, s5: 0xD291C4)),

            // Indigo-and-orange. Same token structure, cooler chrome; the alternate for
            // anyone who does not want the charcoal sheet.
            ["notes"] = (
                BuildPalette(false,
                    canvasBg: 0xEFF1F7, pageBg: 0xF8F9FC, cardBg: 0xFFFFFF, subtleBg: 0xE8EBF3,
                    insetBg: 0xF2F4FA, scrimBg: 0x66131826,
                    dividerStroke: 0xE2E5EF, controlStroke: 0xD0D5E2, strongStroke: 0xA8AFC2,
                    focusStroke: 0x3C4AC4,
                    textPrimary: 0x14161D, textSecondary: 0x5A6072, textTertiary: 0x6B7285,
                    textDisabled: 0xA2A8B8, textOnAccent: 0xFFFFFF, linkText: 0x3C4AC4,
                    accentFill: 0x4B5BE0, accentFillHover: 0x3F4ED2, accentFillPressed: 0x3542B4,
                    accentTint: 0xE9ECFE, accentTintStroke: 0xC6CCFB, selectionIndicator: 0x4B5BE0,
                    controlFill: 0xFFFFFF, controlFillHover: 0xF2F4FB, hoverOverlay: 0x141B2038,
                    successText: 0x0F7A4A, successTint: 0xE3F6EC,
                    dangerText: 0xC4302A, dangerTint: 0xFDECEA, dangerStroke: 0xF3B7B2,
                    warningText: 0x8A5B00, warningTint: 0xFDF3DF,
                    energyFill: 0xEF6A12, progressTrack: 0xDFE3EF,
                    s1: 0x4B5BE0, s2: 0xEF6A12, s3: 0x0E8A8A, s4: 0x8B47D6, s5: 0xC79100),
                BuildPalette(true,
                    canvasBg: 0x121419, pageBg: 0x171A21, cardBg: 0x1E212A, subtleBg: 0x262A35,
                    insetBg: 0x101217, scrimBg: 0x99000000,
                    dividerStroke: 0x2B303C, controlStroke: 0x3A4050, strongStroke: 0x565E73,
                    focusStroke: 0xA6B0FF,
                    textPrimary: 0xEEF0F6, textSecondary: 0xA9AFC1, textTertiary: 0x838A9E,
                    textDisabled: 0x5C6377, textOnAccent: 0x0B0D14, linkText: 0xA6B0FF,
                    accentFill: 0x7F8CF8, accentFillHover: 0x939EFF, accentFillPressed: 0x6A78E4,
                    accentTint: 0x1E2340, accentTintStroke: 0x343C68, selectionIndicator: 0x8E9BFF,
                    controlFill: 0x262A35, controlFillHover: 0x2F3441, hoverOverlay: 0x14FFFFFF,
                    successText: 0x4FCF90, successTint: 0x13281F,
                    dangerText: 0xFF9A90, dangerTint: 0x331A19, dangerStroke: 0x6E2B27,
                    warningText: 0xE9B45A, warningTint: 0x2E2415,
                    energyFill: 0xFF9351, progressTrack: 0x2E3340,
                    s1: 0x8E9BFF, s2: 0xFF9351, s3: 0x3FC7C7, s4: 0xBE8CF5, s5: 0xE8B84B)),
        };

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
        Color Focus,
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
        Color SelectionIndicator,
        Color Success,
        Color SuccessSoft,
        Color Warning,
        Color WarningSoft,
        Color WarningBorder,
        Color WarningText,
        Color Error,
        Color ErrorSoft,
        Color ErrorBorder,
        Color Info,
        Color InfoSoft,
        Color Energy,
        Color Series1,
        Color Series2,
        Color Series3,
        Color Series4,
        Color Series5);
}

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

    private static readonly Palette Light = new(
        Bg: FromRgb(0xF5F6F8),
        Surface1: FromRgb(0xECEFF3),
        Surface2: FromRgb(0xECEFF3),
        Card: FromRgb(0xFFFFFF),
        Border: FromRgb(0xD3D9E1),
        Text1: FromRgb(0x151A21),
        Text2: FromRgb(0x606B79),
        Text3: FromRgb(0x7C8794),
        Accent: FromRgb(0x3E6FA8),
        AccentSecondary: FromRgb(0x668DBE),
        AccentSoft: FromRgb(0xDCE8F5),
        Success: FromRgb(0x2F7D52),
        Warning: FromRgb(0xA87524),
        Error: FromRgb(0xB84A4A),
        Info: FromRgb(0x3E75A8),
        SuccessSoft: FromArgb(0x1A, 0x2F7D52),
        SuccessBorder: FromArgb(0x40, 0x2F7D52),
        WarningSoft: FromArgb(0x1A, 0xA87524),
        WarningBorder: FromArgb(0x40, 0xA87524),
        ErrorSoft: FromArgb(0x1A, 0xB84A4A),
        ErrorBorder: FromArgb(0x40, 0xB84A4A),
        InfoSoft: FromArgb(0x1A, 0x3E75A8),
        InfoBorder: FromArgb(0x40, 0x3E75A8));

    private static readonly Palette Dark = new(
        Bg: FromRgb(0x101318),
        Surface1: FromRgb(0x171C23),
        Surface2: FromRgb(0x202733),
        Card: FromRgb(0x202733),
        Border: FromRgb(0x303A49),
        Text1: FromRgb(0xE2E7EE),
        Text2: FromRgb(0x9AA6B5),
        Text3: FromRgb(0x6F7C8E),
        Accent: FromRgb(0x78A6D8),
        AccentSecondary: FromRgb(0x96B9E0),
        AccentSoft: FromRgb(0x21354B),
        Success: FromRgb(0x68B487),
        Warning: FromRgb(0xD3A451),
        Error: FromRgb(0xDF7772),
        Info: FromRgb(0x78A6D8),
        SuccessSoft: FromArgb(0x26, 0x68B487),
        SuccessBorder: FromArgb(0x59, 0x68B487),
        WarningSoft: FromArgb(0x26, 0xD3A451),
        WarningBorder: FromArgb(0x59, 0xD3A451),
        ErrorSoft: FromArgb(0x26, 0xDF7772),
        ErrorBorder: FromArgb(0x59, 0xDF7772),
        InfoSoft: FromArgb(0x26, 0x78A6D8),
        InfoBorder: FromArgb(0x59, 0x78A6D8));

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

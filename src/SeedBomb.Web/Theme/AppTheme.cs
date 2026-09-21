using MudBlazor;

namespace DataGen.Web.Theme;

/// <summary>Aurora Rift MudBlazor theme — teal-black base, neon green accent.</summary>
internal static class AppTheme
{
    internal static readonly MudTheme Aurora = new()
    {
        PaletteDark = new PaletteDark
        {
            Black = "#030C0A",
            Background = "#030C0A",
            BackgroundGray = "#0A0818",
            Surface = "#0A0818",
            DrawerBackground = "#030C0A",
            DrawerText = "#E8FFF8",
            DrawerIcon = "#00FF88",
            AppbarBackground = "#030C0A",
            AppbarText = "#E8FFF8",
            TextPrimary = "#E8FFF8",
            TextSecondary = "#3A7848",
            TextDisabled = "rgba(232,255,248,0.30)",
            ActionDefault = "#3A7848",
            ActionDisabled = "rgba(232,255,248,0.26)",
            ActionDisabledBackground = "rgba(232,255,248,0.12)",
            Primary = "#00FF88",
            PrimaryContrastText = "#030C0A",
            PrimaryDarken = "#00CC6A",
            PrimaryLighten = "#33FFA0",
            Secondary = "#FF0080",
            SecondaryContrastText = "#E8FFF8",
            Tertiary = "#00CCFF",
            TertiaryContrastText = "#030C0A",
            Info = "#00CCFF",
            InfoContrastText = "#030C0A",
            Success = "#00EE66",
            SuccessContrastText = "#030C0A",
            Warning = "#FFCC00",
            WarningContrastText = "#030C0A",
            Error = "#FF2244",
            ErrorContrastText = "#E8FFF8",
            Dark = "#181E2E",
            DarkContrastText = "#E8FFF8",
            LinesDefault = "#181E2E",
            LinesInputs = "#181E2E",
            Divider = "#181E2E",
            DividerLight = "#181E2E",
            TableLines = "#181E2E",
            OverlayDark = "rgba(3,12,10,0.92)",
            OverlayLight = "rgba(10,8,24,0.75)",
            GrayDefault = "#3A7848",
            GrayLight = "#181E2E",
            GrayLighter = "#0A0818",
            GrayDark = "#3A7848",
            GrayDarker = "#181E2E",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["JetBrains Mono", "monospace"],
                FontSize = "0.875rem",
                LineHeight = "1.6",
            },
            H5 = new H5Typography
            {
                FontFamily = ["Syncopate", "sans-serif"],
                FontWeight = "400",
                LetterSpacing = "0.06em",
            },
            H6 = new H6Typography
            {
                FontFamily = ["Syncopate", "sans-serif"],
                FontWeight = "700",
                LetterSpacing = "0.1em",
            },
            Subtitle1 = new Subtitle1Typography
            {
                FontFamily = ["Syncopate", "sans-serif"],
                FontSize = "0.78rem",
                FontWeight = "700",
                LetterSpacing = "0.1em",
            },
            Subtitle2 = new Subtitle2Typography
            {
                FontFamily = ["Syncopate", "sans-serif"],
                FontSize = "0.68rem",
                LetterSpacing = "0.08em",
            },
            Button = new ButtonTypography
            {
                FontFamily = ["Syncopate", "sans-serif"],
                FontSize = "0.68rem",
                FontWeight = "700",
                LetterSpacing = "0.16em",
            },
            Caption = new CaptionTypography
            {
                FontFamily = ["JetBrains Mono", "monospace"],
                FontSize = "0.72rem",
                LineHeight = "1.5",
            },
        },
    };
}

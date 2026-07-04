using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;

namespace Seedbomb.Services;

/// <summary>
/// Owns the system tray icon. Switches between the colored and white glyph variants to match
/// the current Windows light/dark app theme, live — not just at startup.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string ColoredIconUri = "pack://application:,,,/seedbomb_tray_colored.ico";
    private const string WhiteIconUri = "pack://application:,,,/seedbomb_tray_white.ico";
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly TaskbarIcon _trayIcon;

    /// <summary>Creates and shows the tray icon.</summary>
    public TrayIconService()
    {
        _trayIcon = new TaskbarIcon
        {
            IconSource = LoadIconSource(),
            ToolTipText = "SeedBomb - Mock Data Explosion",
            ContextMenu = BuildContextMenu(),
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => RestoreMainWindow();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _trayIcon.Dispose();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            _trayIcon.IconSource = LoadIconSource();
    }

    private static ContextMenu BuildContextMenu()
    {
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => RestoreMainWindow();

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(exit);
        return menu;
    }

    private static void RestoreMainWindow()
    {
        if (Application.Current.MainWindow is not { } window) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static BitmapImage LoadIconSource() =>
        new(new Uri(IsWindowsDarkMode() ? WhiteIconUri : ColoredIconUri));

    private static bool IsWindowsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
        return key?.GetValue("AppsUseLightTheme") is int appsUseLightTheme && appsUseLightTheme == 0;
    }
}

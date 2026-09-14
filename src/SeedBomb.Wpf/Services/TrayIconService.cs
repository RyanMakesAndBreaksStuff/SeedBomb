using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;

namespace Seedbomb.Services;

/// <summary>
/// Owns the system tray icon. Switches between the colored and white glyph variants to match
/// the current Windows light/dark app theme, live — not just at startup.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string LightBackgroundIconUri = "pack://application:,,,/Resources/logo.ico";
    private const string DarkBackgroundIconUri = "pack://application:,,,/Resources/logo-dark.ico";
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly TaskbarIcon _trayIcon;
    private bool _disposed;

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
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe first so a late SystemEvents callback cannot touch a disposed icon.
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        try
        {
            _trayIcon.Dispose();
        }
        catch
        {
            // Shell/RPC teardown can throw on exit (0x6BA / 0x71A); ignore during shutdown.
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || e.Category != UserPreferenceCategory.General)
            return;

        // SystemEvents fires on a thread-pool thread — marshal icon updates to the UI dispatcher.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        if (dispatcher.CheckAccess())
            ApplyThemeIcon();
        else
            dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyThemeIcon);
    }

    private void ApplyThemeIcon()
    {
        if (_disposed) return;
        try
        {
            _trayIcon.IconSource = LoadIconSource();
        }
        catch
        {
            // Ignore if icon already torn down during shutdown.
        }
    }

    private static ContextMenu BuildContextMenu()
    {
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => RestoreMainWindow();

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current?.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(exit);
        return menu;
    }

    private static void RestoreMainWindow()
    {
        if (Application.Current?.MainWindow is not { } window) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static BitmapImage LoadIconSource() =>
        new(new Uri(IsWindowsDarkMode() ? DarkBackgroundIconUri : LightBackgroundIconUri));

    private static bool IsWindowsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
        return key?.GetValue("AppsUseLightTheme") is int appsUseLightTheme && appsUseLightTheme == 0;
    }
}

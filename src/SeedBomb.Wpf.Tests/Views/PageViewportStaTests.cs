using SeedBomb.Wpf.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Dataverse;
using Seedbomb.Services.Settings;
using Seedbomb.ViewModels;
using Seedbomb.Views.Pages;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;

namespace SeedBomb.Wpf.Tests.Views;

/// <summary>
/// Connections and Settings overflow the launch window when WPF-UI's Frame measures
/// the Page with infinite height. These tests host each page in a ScrollViewer (same
/// infinite-measure axis) sized like the shell content area and assert the footers
/// stay inside that viewport.
/// </summary>
[Collection("StaUi")]
public sealed class PageViewportStaTests : IDisposable
{
    private readonly List<Window> _windows = [];

    // 1100×800 launch minus 220 nav pane and 40 title bar, then a 150%-scale clamp.
    private const double LaunchContentWidth = 840;
    private const double LaunchContentHeight = 680;

    // MinWidth 960 / MinHeight 560 minus the same chrome.
    private const double MinContentWidth = 720;
    private const double MinContentHeight = 500;

    [StaFact]
    public void ConnectionsPage_EditorFooter_StaysInsideLaunchViewport()
    {
        var (page, host) = LoadConnections(LaunchContentWidth, LaunchContentHeight);
        AssertPinnedToHost(page, host);
        AssertInsideHost(page, host, "EditorFooter");
    }

    [StaFact]
    public void ConnectionsPage_EditorFooter_StaysInsideMinViewport()
    {
        var (page, host) = LoadConnections(MinContentWidth, MinContentHeight);
        AssertPinnedToHost(page, host);
        AssertInsideHost(page, host, "EditorFooter");
        Assert.True(page.ActualWidth <= host.ActualWidth + 1.5,
            $"Page width {page.ActualWidth} exceeds host {host.ActualWidth}.");
    }

    [StaFact]
    public void SettingsPage_Footer_StaysInsideLaunchViewport()
    {
        var (page, host) = LoadSettings(LaunchContentWidth, LaunchContentHeight);
        AssertPinnedToHost(page, host);
        AssertInsideHost(page, host, "PageFooter");
        var footer = Assert.IsAssignableFrom<DependencyObject>(page.FindName("PageFooter"));
        Assert.Contains(FindButtons(footer), b => Equals(b.Content, "Save settings"));
    }

    [StaFact]
    public void SettingsPage_Footer_StaysInsideMinViewport()
    {
        var (page, host) = LoadSettings(MinContentWidth, MinContentHeight);
        AssertPinnedToHost(page, host);
        AssertInsideHost(page, host, "PageFooter");
    }

    [StaFact]
    public void AboutPage_Header_StaysInsideLaunchViewport()
    {
        var (page, host) = LoadAbout(LaunchContentWidth, LaunchContentHeight);
        AssertPinnedToHost(page, host);
        AssertInsideHost(page, host, "PageHeader");
        Assert.False(ScrollViewer.GetCanContentScroll(page));
    }

    [StaFact]
    public void AboutPage_Header_StaysInsideMinViewport()
    {
        var (page, host) = LoadAbout(MinContentWidth, MinContentHeight);
        AssertPinnedToHost(page, host);
        AssertInsideHost(page, host, "PageHeader");
        Assert.False(ScrollViewer.GetCanContentScroll(page));
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            window.Close();
        _windows.Clear();
    }

    private (ConnectionsPage Page, Frame Host) LoadConnections(double width, double height)
    {
        EnsureApplication();
        var vm = new ConnectionManagerViewModel(
            Mock.Of<IConnectionProfileService>(),
            Mock.Of<IAuthService>(),
            Mock.Of<IDataverseConnectionService>());
        var page = new ConnectionsPage(vm);
        var host = Host(page, width, height);
        vm.NewProfileCommand.Execute(null);
        page.UpdateLayout();
        host.UpdateLayout();
        Flush();
        return (page, host);
    }

    private (AboutPage Page, Frame Host) LoadAbout(double width, double height)
    {
        EnsureApplication();
        var page = new AboutPage(AboutViewModelTests.Create());
        var host = Host(page, width, height);
        page.UpdateLayout();
        host.UpdateLayout();
        Flush();
        return (page, host);
    }

    private (SettingsPage Page, Frame Host) LoadSettings(double width, double height)
    {
        EnsureApplication();
        var vm = new SettingsViewModel(
            Mock.Of<ISettingsService>(),
            NullLogger<SettingsViewModel>.Instance);
        var page = new SettingsPage(vm);
        var host = Host(page, width, height);
        page.UpdateLayout();
        host.UpdateLayout();
        Flush();
        return (page, host);
    }

    private Frame Host(Page page, double width, double height)
    {
        var host = new Frame
        {
            Width = width,
            Height = height,
            NavigationUIVisibility = NavigationUIVisibility.Hidden,
        };
        host.Navigate(page);
        var window = new Window
        {
            Content = host,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.ToolWindow,
        };
        _windows.Add(window);
        window.Show();
        page.UpdateLayout();
        host.UpdateLayout();
        window.UpdateLayout();
        Flush();
        Flush();
        return host;
    }

    private static void AssertPinnedToHost(Page page, FrameworkElement host)
    {
        Assert.False(double.IsNaN(page.Height), "Page Height must be pinned to the host.");
        Assert.InRange(page.ActualHeight, host.ActualHeight - 2, host.ActualHeight + 2);
    }

    private static void AssertInsideHost(Page page, FrameworkElement host, string footerName)
    {
        var footer = Assert.IsAssignableFrom<FrameworkElement>(page.FindName(footerName));
        Assert.True(footer.ActualHeight > 0, $"{footerName} did not layout.");
        var bottom = footer.TranslatePoint(new Point(0, footer.ActualHeight), host);
        Assert.True(bottom.Y <= host.ActualHeight + 1.5,
            $"{footerName} bottom {bottom.Y:0.#} exceeds host height {host.ActualHeight:0.#}.");
        var right = footer.TranslatePoint(new Point(footer.ActualWidth, 0), host);
        Assert.True(right.X <= host.ActualWidth + 1.5,
            $"{footerName} right {right.X:0.#} exceeds host width {host.ActualWidth:0.#}.");
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
                yield return button;
            foreach (var nested in FindButtons(child))
                yield return nested;
        }
    }

    private static void Flush() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void EnsureApplication()
    {
        if (Application.Current is not null)
            return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SeedBomb;component/Resources/Shared.xaml", UriKind.Absolute),
        });
    }
}

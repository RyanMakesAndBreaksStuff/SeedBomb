using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Seedbomb.Views.Pages;

/// <summary>
/// WPF-UI's Frame measures a <see cref="Page"/> with infinite height, so star rows
/// grow to content and footers fall off the window. Pin <see cref="FrameworkElement.Height"/>
/// to the host's <see cref="FrameworkElement.ActualHeight"/> on Loaded.
/// Pair with <c>ScrollViewer.CanContentScroll="False"</c> on the Page so WPF-UI drops
/// its <c>DynamicScrollViewer</c> (same infinite-measure trap).
/// </summary>
internal static class NavigationPageLayout
{
    /// <summary>Binds <paramref name="page"/> height to its host's actual height.</summary>
    public static void PinHeightToHost(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!TryPin(page))
            page.Dispatcher.BeginInvoke(() => TryPin(page), DispatcherPriority.Loaded);
    }

    private static bool TryPin(Page page)
    {
        // WPF-UI's NavigationViewContentPresenter is the logical Parent. A stock Frame
        // leaves Parent null and parents the Page visually instead.
        var host = page.Parent as FrameworkElement
                   ?? VisualTreeHelper.GetParent(page) as FrameworkElement;
        if (host is null)
            return false;

        page.SetBinding(FrameworkElement.HeightProperty,
            new Binding(nameof(FrameworkElement.ActualHeight)) { Source = host });
        return true;
    }
}
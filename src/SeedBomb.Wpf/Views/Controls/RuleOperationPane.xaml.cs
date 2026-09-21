using System.Windows;
using System.Windows.Controls;

namespace SeedBomb.Views.Controls;

/// <summary>Centre pane of the Rules page: operation picker and the active editor template.</summary>
public partial class RuleOperationPane : UserControl
{
    /// <summary>Creates the pane. DataContext is inherited from the host page.</summary>
    public RuleOperationPane() => InitializeComponent();

    // ContextMenu only opens on right-click by default — open it on left-click instead.
    private void OnMoreButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
using SeedBomb.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SeedBomb.Views.Pages;

/// <summary>Displays reverse-chronological generation run history with search and CSV export.</summary>
public partial class HistoryPage : Page
{
    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public HistoryPage(HistoryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);

    // ponytail: widths reset on each visit (page is Transient); persist them to settings if users ask.
    // GridSplitter can't do this: between two pixel columns it always resizes the left one, which
    // makes a right-anchored boundary stand still while its neighbour grows away from the mouse.
    private void OnColumnGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        var grip = (Thumb)sender;
        var column = HeaderGrid.ColumnDefinitions[Grid.GetColumn(grip)];
        var delta = grip.HorizontalAlignment == HorizontalAlignment.Left ? -e.HorizontalChange : e.HorizontalChange;
        // Growth comes out of the star Tables column; stop at its MinWidth rather than overflow the card.
        delta = Math.Min(delta, Math.Max(0, TablesColumn.ActualWidth - TablesColumn.MinWidth));
        column.Width = new GridLength(Math.Max(column.MinWidth, column.ActualWidth + delta));
    }
}

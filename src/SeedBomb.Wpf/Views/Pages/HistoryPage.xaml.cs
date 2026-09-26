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
    // GridSplitter can't do this: between two pixel columns it only resizes the left one and lets the star
    // Tables column absorb the change, so the dragged boundary stands still. Trading width between the two
    // neighbours keeps it under the mouse and doesn't depend on Tables having any slack.
    private void OnColumnGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        var grip = (Thumb)sender;
        // Time's grip is on its right edge; every other grip is on its own column's left edge.
        var index = Grid.GetColumn(grip) + (grip.HorizontalAlignment == HorizontalAlignment.Right ? 1 : 0);
        var left = HeaderGrid.ColumnDefinitions[index - 1];
        var right = HeaderGrid.ColumnDefinitions[index];
        var delta = Math.Clamp(e.HorizontalChange,
            Math.Min(0, left.MinWidth - left.ActualWidth),
            Math.Max(0, right.ActualWidth - right.MinWidth));
        // The star Tables column resizes itself; only the pixel columns need a new Width.
        if (left != TablesColumn) left.Width = new GridLength(left.ActualWidth + delta);
        if (right != TablesColumn) right.Width = new GridLength(right.ActualWidth - delta);
    }

    // A header wider than the card clips its right-hand columns and pins Tables at MinWidth, where its grips
    // stop tracking the mouse. Squeeze the pixel columns toward their MinWidth, in proportion, until it fits.
    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var pixelColumns = HeaderGrid.ColumnDefinitions.Where(c => c != TablesColumn).ToList();
        var excess = pixelColumns.Sum(c => c.ActualWidth) + TablesColumn.MinWidth
                     - LayoutInformation.GetLayoutSlot(HeaderGrid).Width;
        var room = pixelColumns.Sum(c => c.ActualWidth - c.MinWidth);
        if (excess <= 0 || room <= 0) return;

        var scale = Math.Min(1, excess / room);
        foreach (var column in pixelColumns)
            column.Width = new GridLength(column.ActualWidth - (column.ActualWidth - column.MinWidth) * scale);
    }
}

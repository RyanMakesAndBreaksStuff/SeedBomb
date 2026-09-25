using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace SeedBomb.Views.Controls;

/// <summary>Left pane of the Rules page: filter chips and the grouped column list.</summary>
public partial class RuleColumnListControl : UserControl
{
    // ponytail: view-only state keyed by group name; GroupItems are rebuilt on every refresh.
    private readonly HashSet<object> _collapsedGroups = [];

    /// <summary>Creates the pane. DataContext is inherited from the host page.</summary>
    public RuleColumnListControl() => InitializeComponent();

    // Loaded covers regenerated containers; DataContextChanged covers recycled ones.
    private void OnGroupHeaderLoaded(object sender, RoutedEventArgs e) => SyncHeader(sender);

    private void OnGroupHeaderDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SyncHeader(sender);

    private void SyncHeader(object sender)
    {
        if (sender is ToggleButton { DataContext: CollectionViewGroup group } header)
            header.IsChecked = !_collapsedGroups.Contains(group.Name);
    }

    private void OnGroupHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: CollectionViewGroup group } header)
            return;
        if (header.IsChecked == true)
            _collapsedGroups.Remove(group.Name);
        else
            _collapsedGroups.Add(group.Name);
    }
}

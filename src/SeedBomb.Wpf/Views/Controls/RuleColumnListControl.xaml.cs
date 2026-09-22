using System.Windows.Controls;

namespace SeedBomb.Views.Controls;

/// <summary>Left pane of the Rules page: filter chips and the grouped column list.</summary>
public partial class RuleColumnListControl : UserControl
{
    /// <summary>Creates the pane. DataContext is inherited from the host page.</summary>
    public RuleColumnListControl() => InitializeComponent();
}
using System.Windows.Controls;

namespace Seedbomb.Views.Controls;

/// <summary>Right pane of the Rules page: live preview rows and per-rule save/cancel.</summary>
public partial class RulePreviewPane : UserControl
{
    /// <summary>Creates the pane. DataContext is inherited from the host page.</summary>
    public RulePreviewPane() => InitializeComponent();
}

using System.Windows.Controls;

namespace Seedbomb.Views.Controls;

/// <summary>
/// In-progress run sheet (1a/3c). Hosted as an overlay child of GeneratePage, not a window,
/// so the wizard stays visible behind the scrim. DataContext is supplied by the host.
/// </summary>
public partial class RunSheet : UserControl
{
    /// <summary>Initialises the control.</summary>
    public RunSheet() => InitializeComponent();
}

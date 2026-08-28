using System.Windows.Controls;

namespace Seedbomb.Views.Controls;

/// <summary>
/// Run sheet (1a/3c). Hosted as an overlay child of GeneratePage, not a window, so the wizard
/// stays visible behind the scrim. DataContext is supplied by the host. Visibility follows
/// <c>RunViewModel.IsSheetVisible</c>: while <c>IsRunning</c> it shows live progress with a
/// Cancel button; when "Keep window open after finish" is set, the completed sheet holds open
/// (Cancel becomes Close) until the user dismisses it via <c>CloseSheetCommand</c>.
/// </summary>
public partial class RunSheet : UserControl
{
    /// <summary>Initialises the control.</summary>
    public RunSheet() => InitializeComponent();
}

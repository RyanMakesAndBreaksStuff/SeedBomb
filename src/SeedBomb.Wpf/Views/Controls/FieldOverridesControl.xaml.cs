using SeedBomb.ViewModels.Controls;
using System.Windows.Controls;

namespace SeedBomb.Views.Controls;

/// <summary>
/// Displays a <see cref="Wpf.Ui.Controls.NumberBox"/> per selected entity so the user
/// can override the per-entity record count before generating.
/// </summary>
public partial class FieldOverridesControl : UserControl
{
    private readonly FieldOverridesViewModel _vm = new();

    /// <summary>Gets the underlying ViewModel for parent attachment.</summary>
    public FieldOverridesViewModel ViewModel => _vm;

    /// <summary>Initialises the control.</summary>
    public FieldOverridesControl()
    {
        DataContext = _vm;
        InitializeComponent();
    }
}
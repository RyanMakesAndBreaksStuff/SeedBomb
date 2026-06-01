using System.Windows.Controls;
using DataGen.Core.Contracts;
using DataGen.Desktop.ViewModels.Controls;

namespace DataGen.Desktop.Views.Controls;

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

    /// <summary>
    /// Updates the displayed entries to match <paramref name="entities"/>.
    /// Existing counts are preserved for entities that remain selected.
    /// </summary>
    /// <param name="entities">Currently selected entities.</param>
    public void SetEntities(IReadOnlyList<EntitySummary> entities) =>
        _vm.SetEntities(entities);

    /// <summary>Returns the configured record count keyed by entity logical name.</summary>
    public IReadOnlyDictionary<string, int> GetCounts() => _vm.GetCounts();
}

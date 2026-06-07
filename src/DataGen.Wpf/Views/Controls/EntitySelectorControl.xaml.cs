using System.Windows;
using System.Windows.Controls;
using DataGen.Core.Contracts;
using DataGen.Desktop.ViewModels.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace DataGen.Desktop.Views.Controls;

/// <summary>
/// Lists Dataverse user entities with multi-select. Raises
/// <see cref="SelectedEntitiesChanged"/> when selection changes.
/// </summary>
public partial class EntitySelectorControl : UserControl
{
    private EntitySelectorViewModel? _vm;

    /// <summary>Raised when the entity selection changes.</summary>
    public event EventHandler<IReadOnlyList<EntitySummary>>? SelectedEntitiesChanged;

    /// <summary>Initialises the control.</summary>
    public EntitySelectorControl() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is EntitySelectorViewModel)
            return; // already set (e.g. in test or design-time)

        _vm = ((App)Application.Current).Services.GetRequiredService<EntitySelectorViewModel>();
        _vm.SelectedEntitiesChanged += (_, entities) => SelectedEntitiesChanged?.Invoke(this, entities);
        DataContext = _vm;
        _vm.LoadEntitiesCommand.Execute(null);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.AddedItems.OfType<DataGen.Core.Contracts.EntitySummary>())
            _vm?.ToggleSelection(item);
        foreach (var item in e.RemovedItems.OfType<DataGen.Core.Contracts.EntitySummary>())
            _vm?.ToggleSelection(item);
    }

    /// <summary>Clears the visible selection and view-model selection.</summary>
    public void ClearSelection()
    {
        EntityList.SelectedItems.Clear();
        _vm?.ClearSelection();
    }
}

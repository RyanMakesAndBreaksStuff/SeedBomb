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
    /// <summary>Gets the view-model owned by this control.</summary>
    public EntitySelectorViewModel? ViewModel
    {
        get => (EntitySelectorViewModel?)GetValue(ViewModelProperty);
        private set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Identifies the <see cref="ViewModel"/> dependency property.</summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(EntitySelectorViewModel),
            typeof(EntitySelectorControl),
            new PropertyMetadata(null));

    /// <summary>Raised when the entity selection changes.</summary>
    public event EventHandler<IReadOnlyList<EntitySummary>>? SelectedEntitiesChanged;

    /// <summary>Initialises the control.</summary>
    public EntitySelectorControl() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
            return;

        ViewModel = ((App)Application.Current).Services.GetRequiredService<EntitySelectorViewModel>();
        ViewModel.SelectedEntitiesChanged += (_, entities) => SelectedEntitiesChanged?.Invoke(this, entities);
        ViewModel.LoadEntitiesCommand.Execute(null);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.AddedItems.OfType<DataGen.Core.Contracts.EntitySummary>())
            ViewModel?.ToggleSelection(item);
        foreach (var item in e.RemovedItems.OfType<DataGen.Core.Contracts.EntitySummary>())
            ViewModel?.ToggleSelection(item);
    }

    /// <summary>Clears the visible selection and view-model selection.</summary>
    public void ClearSelection()
    {
        EntityList.SelectedItems.Clear();
        ViewModel?.ClearSelection();
    }
}

using System.Windows;
using System.Windows.Controls;
using DataGen.Core.Contracts;
using DataGen.Desktop.ViewModels;

namespace DataGen.Desktop.Views.Pages;

/// <summary>
/// Three-step wizard page: select entities → configure counts → execute generation.
/// </summary>
public partial class GeneratePage : Page
{
    private readonly GenerateViewModel _vm;

    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public GeneratePage(GenerateViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = _vm;
        InitializeComponent();

        _vm.AttachFieldOverrides(FieldOverridesCtrl.ViewModel);
    }

    private void OnEntitiesChanged(object sender, IReadOnlyList<EntitySummary> entities) =>
        _vm.OnEntitiesChanged(entities);

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.ResetCommand.CanExecute(null))
            return;

        EntitySelectorCtrl.ClearSelection();
        _vm.ResetCommand.Execute(null);
    }
}

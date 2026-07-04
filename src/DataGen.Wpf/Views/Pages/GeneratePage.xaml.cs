using System.Windows.Controls;
using DataGen.Core.Contracts;
using Seedbomb.ViewModels;

namespace Seedbomb.Views.Pages;

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

    /// <summary>Resets the wizard back to step one. Invoked from the "Reset" nav bar item in <see cref="Windows.MainWindow"/>.</summary>
    public void Reset()
    {
        if (!_vm.ResetCommand.CanExecute(null))
            return;

        EntitySelectorCtrl.ClearSelection();
        _vm.ResetCommand.Execute(null);
    }
}

using System.Windows.Controls;
using DataGen.Core.Contracts;
using Seedbomb.ViewModels;

namespace Seedbomb.Views.Pages;

/// <summary>
/// Five-step wizard page: select entities → configure counts → field rules → review → execute.
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
        _vm.AttachFieldRules(FieldRulesCtrl.ViewModel);
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

    /// <summary>Resets the wizard and reloads the entity picker after an org switch.</summary>
    public void ReloadForConnectionSwitch()
    {
        if (_vm.ResetCommand.CanExecute(null))
        {
            EntitySelectorCtrl.ClearSelection();
            _vm.ResetCommand.Execute(null);
        }

        EntitySelectorCtrl.ViewModel?.LoadEntitiesCommand.Execute(null);
    }
}

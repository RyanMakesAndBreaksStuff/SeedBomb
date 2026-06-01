using System.Windows;
using System.Windows.Controls;
using DataGen.Core.Contracts;
using DataGen.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DataGen.Wpf.Views.Pages;

/// <summary>
/// Three-step wizard page: select entities → configure counts → execute generation.
/// </summary>
public partial class GeneratePage : Page
{
    private readonly GenerateViewModel _vm;

    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public GeneratePage()
    {
        _vm = ((App)Application.Current).Services.GetRequiredService<GenerateViewModel>();
        DataContext = _vm;
        InitializeComponent();

        _vm.AttachFieldOverrides(FieldOverridesCtrl.ViewModel);
    }

    private void OnEntitiesChanged(object sender, IReadOnlyList<EntitySummary> entities) =>
        _vm.OnEntitiesChanged(entities);
}

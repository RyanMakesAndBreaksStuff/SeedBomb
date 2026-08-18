using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DataGen.Core.Contracts;
using Seedbomb.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>
/// Four-step wizard page: tables → volume &amp; rules → review → run.
/// </summary>
public partial class GeneratePage : Page, INavigableView<GenerateViewModel>
{
    private readonly GenerateViewModel _vm;

    /// <inheritdoc />
    public GenerateViewModel ViewModel => _vm;

    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public GeneratePage(GenerateViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = _vm;
        InitializeComponent();
        Loaded += OnPageLoaded;

        _vm.AttachFieldOverrides(FieldOverridesCtrl.ViewModel);
        // Do not new FieldRulesViewModel here. GenerateViewModel already owns one.
    }

    // Page is not a visual descendant of Frame, so FindAncestor Frame never binds.
    // Logical Parent is NavigationViewContentPresenter; pin Height to its viewport.
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (Parent is FrameworkElement host)
            SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = host });

        // EntitySelectorCtrl resolves its ViewModel lazily on its own Loaded, which fires before
        // this page's Loaded — safe to attach here.
        if (EntitySelectorCtrl.ViewModel is { } selectorVm)
            _vm.AttachEntitySelector(selectorVm);
    }

    private void OnEntitiesChanged(object sender, IReadOnlyList<EntitySummary> entities) =>
        _vm.OnEntitiesChanged(entities);

    /// <summary>Resets the wizard back to step one. Invoked from the "Reset" nav bar item in <see cref="Windows.MainWindow"/>.</summary>
    public void Reset()
    {
        if (!_vm.ResetCommand.CanExecute(null))
            return;
        EntitySelectorCtrl.ClearSelection();
        _ = _vm.ResetCommand.ExecuteAsync(null);
    }

    /// <summary>Resets the wizard and reloads the entity picker after an org switch.</summary>
    public void ReloadForConnectionSwitch()
    {
        EntitySelectorCtrl.ClearSelection();
        _ = _vm.ResetWithoutPromptAsync();
        EntitySelectorCtrl.ViewModel?.LoadEntitiesCommand.Execute(null);
    }
}

using DataGen.Core.Contracts;
using Seedbomb.ViewModels;
using System.Windows;
using System.Windows.Controls;
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
        NavigationPageLayout.PinHeightToHost(this);

        // EntitySelectorCtrl usually resolves its ViewModel on its own Loaded before this page's
        // Loaded fires, but that ordering isn't guaranteed (and both this page and the VM are
        // DI singletons, so a missed attach on the first navigation never gets retried on later
        // ones — Loaded/Unloaded do refire per navigation, but EntitySelectorControl.OnLoaded only
        // resolves its ViewModel once). Fall back to attaching once the control's own Loaded fires.
        if (EntitySelectorCtrl.ViewModel is { } selectorVm)
            _vm.AttachEntitySelector(selectorVm);
        else
            EntitySelectorCtrl.Loaded += OnEntitySelectorCtrlLoaded;
    }

    private void OnEntitySelectorCtrlLoaded(object sender, RoutedEventArgs e)
    {
        EntitySelectorCtrl.Loaded -= OnEntitySelectorCtrlLoaded;
        if (EntitySelectorCtrl.ViewModel is { } selectorVm)
            _vm.AttachEntitySelector(selectorVm);
    }

    private void OnEntitiesChanged(object sender, IReadOnlyList<EntitySummary> entities) =>
        _vm.OnEntitiesChanged(entities);

    /// <summary>Resets the wizard and reloads the entity picker after an org switch.</summary>
    public void ReloadForConnectionSwitch()
    {
        EntitySelectorCtrl.ClearSelection();
        _ = _vm.ResetWithoutPromptAsync();
        EntitySelectorCtrl.ViewModel?.LoadEntitiesCommand.Execute(null);
    }
}
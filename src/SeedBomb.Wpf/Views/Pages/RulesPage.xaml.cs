using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Seedbomb.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>
/// Rules editor as a full page (1a/3b). Previously a dialog owned by the Generate flow.
/// Navigation parameter carries the profile + table to edit.
/// </summary>
public partial class RulesPage : Page, INavigableView<RuleEditorViewModel>, INavigationAware
{
    private CancellationTokenSource? _loadCts;

    /// <inheritdoc />
    public RuleEditorViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    public RulesPage(RuleEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    // Same Frame-host trap as GeneratePage: pin Height so the column list can scroll.
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (Parent is FrameworkElement host)
            SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = host });
    }

    // ContextMenu only opens on right-click by default — open it on left-click instead.
    private void OnMoreButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <inheritdoc />
    public async Task OnNavigatedToAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        try
        {
            await ViewModel.LoadForProfileAsync(_loadCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public Task OnNavigatedFromAsync()
    {
        _loadCts?.Cancel();
        return Task.CompletedTask;
    }
}

using SeedBomb.ViewModels;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Abstractions.Controls;

namespace SeedBomb.Views.Pages;

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
    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);

    /// <inheritdoc />
    public async Task OnNavigatedToAsync()
    {
        ViewModel.Activate();
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
        ViewModel.Deactivate();
        return Task.CompletedTask;
    }
}
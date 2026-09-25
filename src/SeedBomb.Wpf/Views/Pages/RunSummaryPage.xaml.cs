using SeedBomb.ViewModels;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Abstractions.Controls;

namespace SeedBomb.Views.Pages;

/// <summary>Completed-run summary, including the failure state (1a/3d).</summary>
public partial class RunSummaryPage : Page, INavigableView<RunViewModel>
{
    /// <inheritdoc />
    public RunViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    public RunSummaryPage(RunViewModel viewModel)
    {
        // Transient page, so each navigation picks up whichever run History (or a new run) selected.
        ViewModel = viewModel.SummaryView;
        DataContext = ViewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);
}
using System.Windows;
using System.Windows.Controls;
using Seedbomb.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>Completed-run summary, including the failure state (1a/3d).</summary>
public partial class RunSummaryPage : Page, INavigableView<RunViewModel>
{
    /// <inheritdoc />
    public RunViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    public RunSummaryPage(RunViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);
}

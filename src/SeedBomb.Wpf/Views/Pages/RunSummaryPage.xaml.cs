using SeedBomb.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Wpf.Ui.Abstractions.Controls;

namespace SeedBomb.Views.Pages;

/// <summary>Completed-run summary, including the failure state (1a/3d).</summary>
public partial class RunSummaryPage : Page, INavigableView<RunViewModel>
{
    private readonly RunViewModel _live;

    /// <inheritdoc />
    public RunViewModel ViewModel => _live.SummaryView;

    /// <summary>Initialises the page.</summary>
    public RunSummaryPage(RunViewModel viewModel)
    {
        _live = viewModel;
        // IN-008: follow SummaryView rather than snapshot it. A run that finishes while this page
        // shows a historical run can't re-navigate here (same page type), so the binding swaps it.
        SetBinding(DataContextProperty, new Binding(nameof(RunViewModel.SummaryView)) { Source = viewModel });
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);
}
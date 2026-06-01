using System.Windows;
using DataGen.Wpf.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace DataGen.Wpf.Views.Windows;

/// <summary>
/// Main application window. Hosts the top-tab <see cref="NavigationView"/>
/// and the three page frames.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;

    /// <summary>Initialises the window, sets DataContext, and wires NavigationView to DI.</summary>
    /// <param name="viewModel">Bound view-model supplying user info for the header.</param>
    /// <param name="serviceProvider">DI container forwarded to NavigationView for page resolution.</param>
    /// <param name="snackbarService">Snackbar service — wired to <see cref="SnackbarPresenter"/> on Loaded.</param>
    /// <param name="contentDialogService">Content dialog service — wired to <see cref="ContentDialogHost"/> on Loaded.</param>
    public MainWindow(
        MainWindowViewModel viewModel,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;

        DataContext = viewModel;
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);

        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _snackbarService.SetSnackbarPresenter(SnackbarPresenter);

        // ContentDialogService.SetDialogControl requires the ContentDialogHost from the visual tree.
        if (Content is System.Windows.Controls.Grid grid)
        {
            foreach (var child in grid.Children.OfType<ContentDialogHost>())
            {
                _contentDialogService.SetDialogControl(child);
                break;
            }
        }
    }
}

using System.Windows;
using System.Windows.Interop;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Navigation;
using Seedbomb.ViewModels;
using Seedbomb.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Seedbomb.Views.Windows;

/// <summary>Main application window. Hosts the 1a left NavigationView.</summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _vm;
    private readonly ConnectionManagerViewModel _connectionManagerViewModel;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly NavigationViewNavigator _navigator;
    private object? _currentPageContent;

    /// <summary>Initialises the window, sets DataContext, and wires NavigationView to DI.</summary>
    public MainWindow(
        MainWindowViewModel viewModel,
        ConnectionManagerViewModel connectionManagerViewModel,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        IAppNavigator navigator)
    {
        _vm = viewModel;
        _connectionManagerViewModel = connectionManagerViewModel;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _navigator = (NavigationViewNavigator)navigator;

        DataContext = viewModel;
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);

        connectionManagerViewModel.ConnectionSwitched += OnConnectionSwitched;
        viewModel.OpenConnectionManagerRequested += (_, _) =>
            _navigator.Navigate(typeof(ConnectionsPage));

        RootNavigation.Navigated += (_, e) =>
        {
            _currentPageContent = e.Page;
            SyncFirstRunOverlay();
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.HasConnection))
                SyncFirstRunOverlay();
        };
        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _navigator.Control = RootNavigation;
        _snackbarService.SetSnackbarPresenter(SnackbarPresenter);

        if (Content is System.Windows.Controls.Grid grid)
        {
            foreach (var child in grid.Children.OfType<ContentDialogHost>())
            {
                _contentDialogService.SetDialogHost(child);
                break;
            }
        }

        _connectionManagerViewModel.ParentHwnd = new WindowInteropHelper(this).Handle;
        await _connectionManagerViewModel.LoadCommand.ExecuteAsync(null);
        _vm.HasConnection = _connectionManagerViewModel.Profiles.Count > 0;
        RootNavigation.Navigate(typeof(GeneratePage));
    }

    private void OnConnectionSwitched(object? sender, (ConnectionProfile Profile, AuthResult Result) e)
    {
        if (!e.Result.Succeeded) return;

        _vm.UserDisplayName = e.Result.DisplayName ?? _vm.UserDisplayName;
        _vm.OrgUrl = e.Profile.EnvironmentUrl;
        _vm.HasConnection = true;
        if (_currentPageContent is GeneratePage page)
            page.ReloadForConnectionSwitch();
        SyncFirstRunOverlay();
    }

    private void SyncFirstRunOverlay()
    {
        var show = !_vm.HasConnection
            && _currentPageContent is not ConnectionsPage
            && _currentPageContent is not SettingsPage;
        FirstRunOverlay.SetCurrentValue(
            VisibilityProperty,
            show ? Visibility.Visible : Visibility.Collapsed);
    }
}

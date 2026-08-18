using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
    private readonly IConnectionProfileService _profileService;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly NavigationViewNavigator _navigator;
    private object? _currentPageContent;

    /// <summary>Initialises the window, sets DataContext, and wires NavigationView to DI.</summary>
    public MainWindow(
        MainWindowViewModel viewModel,
        ConnectionManagerViewModel connectionManagerViewModel,
        IConnectionProfileService profileService,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        IAppNavigator navigator)
    {
        _vm = viewModel;
        _connectionManagerViewModel = connectionManagerViewModel;
        _profileService = profileService;
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
        _profileService.ProfilesChanged += OnProfilesChanged;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
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
        SyncHasConnection();
        RootNavigation.Navigate(typeof(GeneratePage));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
        => _profileService.ProfilesChanged -= OnProfilesChanged;

    // T4: react to saves/deletes (fired by the profile store) instead of ObservableCollection's
    // CollectionChanged. LoadAsync does Profiles.Clear() then re-adds, so watching the collection
    // directly drove HasConnection false for an instant on every routine reload (flashing the
    // first-run overlay). ProfilesChanged can fire on a background thread (the store's disk write
    // uses ConfigureAwait(false) throughout) — marshal to the UI thread before touching the
    // ObservableCollection or any DependencyObject.
    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            _ = ReloadAndSyncHasConnectionAsync();
        else
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(OnProfilesChangedOnUiThread));
    }

    private void OnProfilesChangedOnUiThread() => _ = ReloadAndSyncHasConnectionAsync();

    // HasConnection must be re-derived only after the reload fully settles, not mid-reload —
    // that's the fix for the overlay flash.
    private async Task ReloadAndSyncHasConnectionAsync()
    {
        await _connectionManagerViewModel.LoadCommand.ExecuteAsync(null);
        SyncHasConnection();
    }

    // WR-001: the profile count is the only source of truth. Deleting the last connection
    // must re-show the first-run overlay and re-disable the nav items.
    private void SyncHasConnection()
        => _vm.HasConnection = _connectionManagerViewModel.Profiles.Count > 0;

    private void OnConnectionSwitched(object? sender, (ConnectionProfile Profile, AuthResult Result) e)
    {
        if (!e.Result.Succeeded) return;

        _vm.UserDisplayName = e.Result.DisplayName ?? _vm.UserDisplayName;
        _vm.OrgUrl = e.Profile.EnvironmentUrl;
        SyncHasConnection();
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

using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IAuthService _authService;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly NavigationViewNavigator _navigator;
    private readonly IServiceProvider _serviceProvider;
    private object? _currentPageContent;

    /// <summary>Initialises the window, sets DataContext, and wires NavigationView to DI.</summary>
    public MainWindow(
        MainWindowViewModel viewModel,
        ConnectionManagerViewModel connectionManagerViewModel,
        IConnectionProfileService profileService,
        IAuthService authService,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        IAppNavigator navigator)
    {
        _vm = viewModel;
        _connectionManagerViewModel = connectionManagerViewModel;
        _profileService = profileService;
        _authService = authService;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _navigator = (NavigationViewNavigator)navigator;
        _serviceProvider = serviceProvider;

        DataContext = viewModel;
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);

        connectionManagerViewModel.ConnectionSwitched += OnConnectionSwitched;
        viewModel.OpenConnectionManagerRequested += (_, _) =>
        {
            _navigator.Navigate(typeof(ConnectionsPage));

            // FirstRunOverlay's "Add connection" button reaches this same handler. The pane
            // footer's account button and SignInOverlay's "Manage connections" button also call
            // it, but both are unreachable while HasConnection is false (the overlay's scrim
            // covers the nav, and SignInOverlay only shows when HasConnection is already true) —
            // so HasConnection is false only in the FirstRunOverlay case. Without this,
            // ConnectionsPage lands with its editor pane present but disabled, since only
            // NewProfileCommand sets IsEditing = true.
            if (!_vm.HasConnection)
                _connectionManagerViewModel.NewProfileCommand.Execute(null);
        };
        viewModel.SignInRequested += OnSignInRequested;

        RootNavigation.Navigated += (_, e) =>
        {
            _currentPageContent = e.Page;
            SyncFirstRunOverlay();
            SyncSignInOverlay();
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            // ShowSignInOverlay is derived (HasConnection && NeedsSignIn) and raises its own
            // PropertyChanged via NotifyPropertyChangedFor — a separate, later event than the
            // HasConnection/NeedsSignIn one. SignInOverlay's Visibility binds directly to it, so
            // that later event re-pushes Visible from the binding after SyncSignInOverlay already
            // collapsed it for the current page, unless this also re-syncs on that event.
            if (e.PropertyName is nameof(MainWindowViewModel.HasConnection)
                or nameof(MainWindowViewModel.NeedsSignIn)
                or nameof(MainWindowViewModel.ShowSignInOverlay))
            {
                SyncFirstRunOverlay();
                SyncSignInOverlay();
            }
        };
        _profileService.ProfilesChanged += OnProfilesChanged;
        _authService.SignedOut += OnSignedOut;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;

        // Same shared ConnectionManagerViewModel instance ConnectionsPage's identical
        // SwitchError banner reads — a failed retry here is visible there too. Bound with an
        // explicit Source (not via DataContext) so the binding's source is correct from the
        // first evaluation — see the comment on SignInErrorBanner in the XAML.
        BindingOperations.SetBinding(SignInErrorBanner, VisibilityProperty, new Binding(
            nameof(ConnectionManagerViewModel.HasSwitchError))
        {
            Source = connectionManagerViewModel,
            Converter = (IValueConverter)FindResource("BoolToVisibilityConverter"),
        });
        BindingOperations.SetBinding(SignInErrorText, System.Windows.Controls.TextBlock.TextProperty, new Binding(
            nameof(ConnectionManagerViewModel.SwitchError)) { Source = connectionManagerViewModel });
    }

    private async void OnSignInRequested(object? sender, EventArgs e)
    {
        var last = await _profileService.GetLastUsedAsync();
        if (last is not null)
            await _connectionManagerViewModel.SelectProfileCommand.ExecuteAsync(last);
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

        // Startup's silent sign-in (App.xaml.cs) never goes through SelectProfileAsync, so
        // ConnectedProfileId is only ever set there for later, in-session switches — seed it
        // here for the common case where the silent reconnect already succeeded.
        if (_vm.IsConnected)
        {
            var lastUsed = _connectionManagerViewModel.Profiles.FirstOrDefault(p => p.IsLastUsed);
            if (lastUsed is not null)
                _connectionManagerViewModel.ConnectedProfileId = lastUsed.Id;
        }

        RootNavigation.Navigate(typeof(GeneratePage));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _profileService.ProfilesChanged -= OnProfilesChanged;
        _authService.SignedOut -= OnSignedOut;
    }

    // ProfileAuthService.SignOutAsync uses ConfigureAwait(false) throughout, so SignedOut can
    // fire on a background thread — marshal before touching the ViewModel, same as OnProfilesChanged.
    private void OnSignedOut(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            SignOut();
        else
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(SignOut));

        void SignOut()
        {
            _vm.NeedsSignIn = true;
            _connectionManagerViewModel.ConnectedProfileId = null;
        }
    }

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
        _vm.NeedsSignIn = false;
        SyncHasConnection();

        // GeneratePage and its ViewModel are DI singletons, so the entity picker's "load once on
        // control load" cache otherwise survives a connection switch untouched whenever the switch
        // happens from a different page (the normal case — switching lives on ConnectionsPage).
        // Resolve the singleton directly instead of gating on _currentPageContent so a switch made
        // from elsewhere still clears stale entities before the user next lands on Generate.
        _serviceProvider.GetRequiredService<GeneratePage>().ReloadForConnectionSwitch();
        SyncFirstRunOverlay();
        SyncSignInOverlay();
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

    // Same ConnectionsPage/SettingsPage exclusion as SyncFirstRunOverlay: never cover the
    // page the user needs to interact with to actually sign in.
    private void SyncSignInOverlay()
    {
        var show = _vm.ShowSignInOverlay
            && _currentPageContent is not ConnectionsPage
            && _currentPageContent is not SettingsPage;
        SignInOverlay.SetCurrentValue(
            VisibilityProperty,
            show ? Visibility.Visible : Visibility.Collapsed);
    }
}

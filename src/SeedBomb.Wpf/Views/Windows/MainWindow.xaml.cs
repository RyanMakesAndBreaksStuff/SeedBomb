using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Navigation;
using Seedbomb.ViewModels;
using Seedbomb.Views.Pages;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
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

        DataContext = viewModel;
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);

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
        _authService.SignedOut += OnSignedOut;
        Loaded += OnWindowLoaded;
        SourceInitialized += OnSourceInitialized;
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
            nameof(ConnectionManagerViewModel.SwitchError))
        { Source = connectionManagerViewModel });
    }

    private async void OnSignInRequested(object? sender, EventArgs e)
    {
        try
        {
            var last = await _profileService.GetLastUsedAsync();
            if (last is not null)
                await _connectionManagerViewModel.SelectProfileCommand.ExecuteAsync(last);
        }
        catch (Exception ex)
        {
            // async void: nothing above this frame can catch. SwitchError is already bound to
            // SignInErrorBanner/SignInErrorText via the explicit-source bindings at :100-107.
            _connectionManagerViewModel.SwitchError = ex.Message;
        }
    }

    // The XAML asks for 1380x900 DIPs, which does not fit a 1366x768 display or a 1920x1080 one
    // at 125%+ scaling. WindowStartupLocation="CenterScreen" then centres an oversized window and
    // pushes the title bar off the top and the nav footer off the bottom. Clamp to the work area
    // and re-centre inside it. Startup only: the user's own resizing and maximizing are untouched.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;

        var work = SystemParameters.WorkArea;
        var width = Math.Min(Width, work.Width);
        var height = Math.Min(Height, work.Height);

        // MinWidth/MinHeight win over the clamp in WPF, so a work area smaller than the minimum
        // still overflows — nothing sensible to do there beyond keeping the minimum small.
        Width = width;
        Height = height;
        Left = work.Left + Math.Max(0, (work.Width - width) / 2);
        Top = work.Top + Math.Max(0, (work.Height - height) / 2);
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
        _vm.SyncHasConnection();

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
        _authService.SignedOut -= OnSignedOut;
        _vm.Dispose();
    }

    // ProfileAuthService.SignOutAsync uses ConfigureAwait(false) throughout, so SignedOut can
    // fire on a background thread — marshal before touching the ViewModel.
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

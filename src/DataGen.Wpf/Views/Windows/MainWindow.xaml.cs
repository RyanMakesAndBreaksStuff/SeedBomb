using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Seedbomb.ViewModels;
using Seedbomb.Views.Pages;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Seedbomb.Views.Windows;

/// <summary>
/// Main application window. Hosts the top-tab <see cref="NavigationView"/>
/// and the three page frames.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _vm;
    private readonly ConnectionManagerViewModel _connectionManagerViewModel;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private bool _drawerOpen;
    private object? _currentPageContent;
    private TranslateTransform DrawerTranslate => (TranslateTransform)DrawerControl.RenderTransform;

    /// <summary>Initialises the window, sets DataContext, and wires NavigationView to DI.</summary>
    /// <param name="viewModel">Bound view-model supplying user info for the header.</param>
    /// <param name="connectionManagerViewModel">View-model for the Connection Manager drawer.</param>
    /// <param name="serviceProvider">DI container forwarded to NavigationView for page resolution.</param>
    /// <param name="snackbarService">Snackbar service — wired to <see cref="SnackbarPresenter"/> on Loaded.</param>
    /// <param name="contentDialogService">Content dialog service — wired to <see cref="ContentDialogHost"/> on Loaded.</param>
    public MainWindow(
        MainWindowViewModel viewModel,
        ConnectionManagerViewModel connectionManagerViewModel,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _vm = viewModel;
        _connectionManagerViewModel = connectionManagerViewModel;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;

        DataContext = viewModel;
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);

        DrawerControl.DataContext = connectionManagerViewModel;
        connectionManagerViewModel.DrawerCloseRequested += (_, _) => CloseDrawer();
        connectionManagerViewModel.ConnectionSwitched += OnConnectionSwitched;
        viewModel.OpenConnectionManagerRequested += (_, _) => OpenDrawer();

        // NavigationViewContentPresenter (the internal Frame) is a protected member of NavigationView,
        // so the only public way to know what's currently displayed is the Navigated event.
        RootNavigation.Navigated += (_, e) => _currentPageContent = e.Page;

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
                _contentDialogService.SetDialogHost(child);
                break;
            }
        }

        _connectionManagerViewModel.ParentHwnd = new WindowInteropHelper(this).Handle;
        RootNavigation.Navigate(typeof(GeneratePage));
    }

    private void OnScrimClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        CloseDrawer();

    private void OnConnectionHeaderClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _vm.OpenConnectionManagerCommand.Execute(null);

    // Reset lives in the nav bar chrome, not on GeneratePage itself, so it acts on whichever
    // page is currently hosted rather than navigating anywhere (no TargetPageType on the item).
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_currentPageContent is GeneratePage page)
            page.Reset();
    }

    private void OnConnectionSwitched(object? sender, (ConnectionProfile Profile, AuthResult Result) e)
    {
        if (!e.Result.Succeeded) return;

        _vm.UserDisplayName = e.Result.DisplayName ?? _vm.UserDisplayName;
        _vm.OrgUrl = e.Profile.EnvironmentUrl;
        CloseDrawer();
    }

    /// <summary>Slides the drawer in from the right.</summary>
    private void OpenDrawer()
    {
        if (_drawerOpen) return;
        _drawerOpen = true;
        DrawerScrim.Visibility = Visibility.Visible;

        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>Slides the drawer out to the right.</summary>
    private void CloseDrawer()
    {
        if (!_drawerOpen) return;
        _drawerOpen = false;

        var anim = new DoubleAnimation(500, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => DrawerScrim.Visibility = Visibility.Collapsed;
        DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
    }
}

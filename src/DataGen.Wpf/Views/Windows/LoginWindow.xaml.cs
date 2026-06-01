using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DataGen.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace DataGen.Desktop.Views.Windows;

/// <summary>Sign-in window with Connection Manager drawer.</summary>
public partial class LoginWindow : FluentWindow
{
    private readonly LoginWindowViewModel _vm;
    private bool _drawerOpen;
    private TranslateTransform DrawerTranslate => (TranslateTransform)DrawerControl.RenderTransform;

    /// <summary>Initialises the window and wires the ViewModel.</summary>
    public LoginWindow(LoginWindowViewModel viewModel, ConnectionManagerViewModel connectionManagerViewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        DrawerControl.DataContext = connectionManagerViewModel;
        connectionManagerViewModel.DrawerCloseRequested += (_, _) => CloseDrawer();
        viewModel.LoginSucceeded += OnLoginSucceeded;
        viewModel.OpenConnectionManagerRequested += (_, _) => OpenDrawer();
    }

    private void OnSignInClick(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _vm.LoginCommand.Execute(hwnd);
    }

    private void OnLoginSucceeded(object? sender, string displayName)
    {
        if (Application.Current is App app)
            app.ShowMainWindow(displayName);
        Close();
    }

    private void OnScrimClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => CloseDrawer();

    /// <summary>Slides the drawer in from the right.</summary>
    public void OpenDrawer()
    {
        if (_drawerOpen) return;
        _drawerOpen = true;
        DrawerScrim.Visibility = Visibility.Visible;

        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        DrawerTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

    /// <summary>Slides the drawer out to the right.</summary>
    public void CloseDrawer()
    {
        if (!_drawerOpen) return;
        _drawerOpen = false;

        var anim = new DoubleAnimation(500, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => DrawerScrim.Visibility = Visibility.Collapsed;
        DrawerTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }
}

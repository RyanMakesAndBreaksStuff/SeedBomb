using System.Windows;
using System.Windows.Interop;
using DataGen.Wpf.ViewModels;
using Wpf.Ui.Controls;

namespace DataGen.Wpf.Views.Windows;

/// <summary>
/// Sign-in window shown when no cached MSAL token is available.
/// Passes the native HWND to <see cref="LoginWindowViewModel.LoginCommand"/>
/// so MSAL can parent the browser popup to this window.
/// </summary>
public partial class LoginWindow : FluentWindow
{
    private readonly LoginWindowViewModel _vm;

    /// <summary>Initialises the window and wires the ViewModel.</summary>
    /// <param name="viewModel">View-model that owns the MSAL sign-in command.</param>
    public LoginWindow(LoginWindowViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.LoginSucceeded += OnLoginSucceeded;
    }

    private void OnSignInClick(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _vm.LoginCommand.Execute(hwnd);
    }

    private void OnLoginSucceeded(object? sender, string displayName)
    {
        // Delegate to App so it can show MainWindow.
        if (Application.Current is App app)
            app.ShowMainWindow(displayName);

        Close();
    }
}

using Wpf.Ui.Controls;

namespace DataGen.Wpf.Views.Windows;

/// <summary>
/// Sign-in window shown when no cached MSAL token is available.
/// LoginViewModel and command wiring are added in W1-A.
/// </summary>
public partial class LoginWindow : FluentWindow
{
    /// <summary>Initialises the window.</summary>
    public LoginWindow() => InitializeComponent();
}

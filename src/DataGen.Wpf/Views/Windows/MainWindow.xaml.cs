using DataGen.Wpf.ViewModels;
using Wpf.Ui.Controls;

namespace DataGen.Wpf.Views.Windows;

/// <summary>
/// Main application window. Hosts the top-tab <see cref="Wpf.Ui.Controls.NavigationView"/>
/// and the three page frames.
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>Initialises the window, sets DataContext, and wires NavigationView to DI.</summary>
    /// <param name="viewModel">Bound view-model supplying user info for the header.</param>
    /// <param name="serviceProvider">DI container forwarded to NavigationView for page resolution.</param>
    public MainWindow(MainWindowViewModel viewModel, IServiceProvider serviceProvider)
    {
        DataContext = viewModel;
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);
    }
}

using Seedbomb.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>Settings page with Connection, Appearance, Generation Defaults, and About sections.</summary>
public partial class SettingsPage : Page
{
    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);
}

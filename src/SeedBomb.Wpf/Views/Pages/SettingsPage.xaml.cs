using SeedBomb.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace SeedBomb.Views.Pages;

/// <summary>Settings page with Appearance and Generation Defaults sections.</summary>
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
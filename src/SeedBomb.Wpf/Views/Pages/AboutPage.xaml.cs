using SeedBomb.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace SeedBomb.Views.Pages;

/// <summary>Application identity, project links, and open-source attribution.</summary>
public partial class AboutPage : Page
{
    public AboutPage(AboutViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);
}
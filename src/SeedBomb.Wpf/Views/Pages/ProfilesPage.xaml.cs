using SeedBomb.ViewModels;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Abstractions.Controls;

namespace SeedBomb.Views.Pages;

/// <summary>Profiles library — list and detail (1a/3a).</summary>
public partial class ProfilesPage : Page, INavigableView<ProfilesViewModel>
{
    /// <inheritdoc />
    public ProfilesViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    /// <param name="viewModel">Page view-model.</param>
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);

    // ContextMenu only opens on right-click by default — open it on left-click instead.
    private void OnMoreButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
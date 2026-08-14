using System.Windows.Controls;
using Seedbomb.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>Profiles library — list and detail (1a/3a).</summary>
public partial class ProfilesPage : Page, INavigableView<ProfilesViewModel>
{
    /// <inheritdoc />
    public ProfilesViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }
}

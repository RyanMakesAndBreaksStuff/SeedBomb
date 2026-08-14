using System.Windows.Controls;
using Microsoft.Win32;
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

        viewModel.PickImportPath ??= PickImport;
        viewModel.PickExportPath ??= PickExport;
    }

    private static string? PickImport()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Profile (*.profile.json)|*.profile.json|JSON (*.json)|*.json|All files|*.*",
            Title = "Import profile",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? PickExport(string name)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Profile (*.profile.json)|*.profile.json",
            FileName = $"{name}.profile.json",
            Title = "Export profile",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}

using Microsoft.Win32;
using Seedbomb.Services.Navigation;
using Seedbomb.ViewModels;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>Profiles library — list and detail (1a/3a).</summary>
public partial class ProfilesPage : Page, INavigableView<ProfilesViewModel>
{
    private readonly GenerateViewModel _generate;
    private readonly IAppNavigator _navigator;

    /// <inheritdoc />
    public ProfilesViewModel ViewModel { get; }

    /// <summary>Initialises the page and wires the Generate wizard as the profile host.</summary>
    /// <param name="viewModel">Page view-model.</param>
    /// <param name="generate">Singleton Generate wizard — receives applied profiles.</param>
    /// <param name="navigator">Shell navigator.</param>
    public ProfilesPage(
        ProfilesViewModel viewModel, GenerateViewModel generate, IAppNavigator navigator)
    {
        ViewModel = viewModel;
        _generate = generate;
        _navigator = navigator;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;

        viewModel.PickImportPath ??= PickImport;
        viewModel.PickExportPath ??= PickExport;

        // CR-001: the page is a first-class profile host, not just a browser. Without these
        // the primary Load Profile button silently no-ops.
        viewModel.GetMetadata ??= () => generate.EntityMetadataMap;
        // Metadata is fetched only when the user clicks Load (never on selection), so browsing a
        // profile whose tables are absent from the connected org can't throw a SchemaException.
        viewModel.EnsureMetadata ??= (tables, ct) => generate.EnsureMetadataAsync(tables, ct);
        viewModel.GetRunId ??= () => generate.RunId;
        viewModel.CaptureCurrent ??= generate.BuildProfileSnapshot;
        viewModel.IsBoardDirty ??= generate.IsBoardDirty;
        viewModel.ConfirmOverwrite ??= msg => MessageBox.Show(
            msg, "Load profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        viewModel.ConfirmDelete ??= name => MessageBox.Show(
            $"Delete profile '{name}'? This cannot be undone.",
            "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        viewModel.ProfileApplied += OnProfileApplied;
        Unloaded += (_, _) => viewModel.ProfileApplied -= OnProfileApplied;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);

    private void OnProfileApplied(object? sender, Seedbomb.Services.Profiles.ProfileImportReport report)
    {
        _generate.ApplyImportReport(report);
        _navigator.Navigate(typeof(GeneratePage));
    }

    // ContextMenu only opens on right-click by default — open it on left-click instead.
    private void OnMoreButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
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

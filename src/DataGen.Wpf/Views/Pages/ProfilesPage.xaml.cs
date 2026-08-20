using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Profiles;
using Seedbomb.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>Profiles library — list and detail (1a/3a).</summary>
public partial class ProfilesPage : Page, INavigableView<ProfilesViewModel>
{
    private readonly GenerateViewModel _generate;
    private readonly IAppNavigator _navigator;
    private readonly IProfileService _profiles;
    private CancellationTokenSource? _metadataPrefetchCts;

    /// <inheritdoc />
    public ProfilesViewModel ViewModel { get; }

    /// <summary>Initialises the page and wires the Generate wizard as the profile host.</summary>
    /// <param name="viewModel">Page view-model.</param>
    /// <param name="generate">Singleton Generate wizard — receives applied profiles.</param>
    /// <param name="navigator">Shell navigator.</param>
    /// <param name="profiles">Profile store — used to prefetch live metadata for the selected
    /// profile's tables (T1: a profiles-first "Load Profile" click must not have to wait on a
    /// prior Rules visit to populate <see cref="GenerateViewModel.EntityMetadataMap"/>).</param>
    public ProfilesPage(
        ProfilesViewModel viewModel, GenerateViewModel generate, IAppNavigator navigator, IProfileService profiles)
    {
        ViewModel = viewModel;
        _generate = generate;
        _navigator = navigator;
        _profiles = profiles;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PickImportPath ??= PickImport;
        viewModel.PickExportPath ??= PickExport;

        // CR-001: the page is a first-class profile host, not just a browser. Without these
        // the primary Load Profile button silently no-ops.
        viewModel.GetMetadata ??= () => generate.EntityMetadataMap;
        viewModel.GetRunId ??= () => generate.RunId;
        viewModel.CaptureCurrent ??= generate.BuildProfileSnapshot;
        viewModel.IsBoardDirty ??= generate.IsBoardDirty;
        viewModel.ConfirmOverwrite ??= msg => MessageBox.Show(
            msg, "Load profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        viewModel.ConfirmDelete ??= name => MessageBox.Show(
            $"Delete profile '{name}'? This cannot be undone.",
            "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        viewModel.ProfileApplied += OnProfileApplied;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) =>
        {
            viewModel.ProfileApplied -= OnProfileApplied;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _metadataPrefetchCts?.Cancel();
            _metadataPrefetchCts?.Dispose();
        };
    }

    // T1: viewModel.GetMetadata (wired above) reads generate.EntityMetadataMap synchronously —
    // by the time the user clicks "Load Profile" (LoadCommand -> PresentImport) it must already hold
    // the selected profile's tables, or every table lands in NotImported. GoToRulesAsync only
    // fills that map when the user visits the Rules step first, which a profiles-first flow may
    // never do — so fetch it here, as the profile is selected/presented, ahead of that click.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProfilesViewModel.SelectedItem) || ViewModel.SelectedItem is null)
            return;

        _metadataPrefetchCts?.Cancel();
        _metadataPrefetchCts?.Dispose();
        _metadataPrefetchCts = new CancellationTokenSource();
        _ = PrefetchSelectedProfileMetadataAsync(ViewModel.SelectedItem.Name, _metadataPrefetchCts.Token);
    }

    private async Task PrefetchSelectedProfileMetadataAsync(string profileName, CancellationToken ct)
    {
        try
        {
            var profile = await _profiles.LoadAsync(profileName, ct);
            var tables = profile.Tables.Select(t => t.Table).ToArray();
            if (tables.Length > 0)
                await _generate.EnsureMetadataAsync(tables, ct);
        }
        catch (OperationCanceledException)
        {
            // Selection changed again, or the page navigated away, before the fetch finished.
        }
        catch (Exception ex)
        {
            // ProfilesViewModel.SetError is private; these are the same two properties it sets.
            ViewModel.HasError = true;
            ViewModel.StatusMessage = $"Couldn't load table metadata for “{profileName}”: {ex.Message}";
        }
    }

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

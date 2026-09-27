using SeedBomb.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Abstractions.Controls;

namespace SeedBomb.Views.Pages;

/// <summary>Connection profiles as a footer destination.</summary>
public partial class ConnectionsPage : Page, INavigableView<ConnectionManagerViewModel>, INavigationAware
{
    private CancellationTokenSource? _loadCts;

    /// <inheritdoc />
    public ConnectionManagerViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    public ConnectionsPage(ConnectionManagerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    /// <inheritdoc />
    public async Task OnNavigatedToAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        try
        {
            // RelayCommand special-cases CancellationToken — ExecuteAsync(object) will not forward it.
            await ViewModel.LoadAsync(_loadCts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        SyncClientSecretBox();
    }

    /// <inheritdoc />
    public Task OnNavigatedFromAsync()
    {
        _loadCts?.Cancel();

        // ConnectionsPage is Transient while ConnectionManagerViewModel is Singleton, so a
        // still-showing toast would otherwise reappear on a fresh page instance if the user
        // navigates away and back while it's up.
        // The view-model is a Singleton and this page is Transient, so anything left on it
        // outlives the page - including the decrypted ClientSecret loaded by EditProfileAsync.
        ViewModel.ShowConnectedToast = false;
        // WR-009: Cancel clears the whole editor (IsEditing, IsDirty, TestResult) — clearing
        // EditingProfile alone left an enabled empty form and a Connect button that did nothing.
        ViewModel.CancelCommand.Execute(null);
        return Task.CompletedTask;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e) =>
        NavigationPageLayout.PinHeightToHost(this);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionManagerViewModel.EditingProfile))
            SyncClientSecretBox();
    }

    private void SyncClientSecretBox()
    {
        var secret = ViewModel.EditingProfile?.ClientSecret ?? string.Empty;
        if (ClientSecretBox.Password != secret)
            ClientSecretBox.Password = secret;
    }

    private void OnClientSecretChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel.EditingProfile is { } profile && sender is PasswordBox box)
            profile.ClientSecret = box.Password;
    }
}
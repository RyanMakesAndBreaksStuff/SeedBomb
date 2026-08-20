using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Seedbomb.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>Connection profiles as a footer destination.</summary>
public partial class ConnectionsPage : Page, INavigableView<ConnectionManagerViewModel>, INavigationAware
{
    private CancellationTokenSource? _loadCts;
    private bool _syncingClientSecret;

    /// <inheritdoc />
    public ConnectionManagerViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    public ConnectionsPage(ConnectionManagerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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
        ViewModel.ShowConnectedToast = false;
        return Task.CompletedTask;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionManagerViewModel.EditingProfile))
            SyncClientSecretBox();
    }

    private void SyncClientSecretBox()
    {
        var secret = ViewModel.EditingProfile?.ClientSecret ?? string.Empty;
        if (ClientSecretBox.Password != secret)
        {
            // ConnectionsPage is Transient while ConnectionManagerViewModel is Singleton, so
            // this runs against a brand-new, empty ClientSecretBox on every re-navigation to
            // the page — without the guard, setting Password here fires PasswordChanged and
            // OnClientSecretChanged would mark an untouched profile dirty.
            _syncingClientSecret = true;
            ClientSecretBox.Password = secret;
            _syncingClientSecret = false;
        }
    }

    private void OnClientSecretChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel.EditingProfile is { } profile && sender is PasswordBox box)
            profile.ClientSecret = box.Password;
        if (!_syncingClientSecret)
            MarkDirty();
    }

    private void OnProfileFieldChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.RefreshEnvironmentUrlValidation();
        MarkDirty();
    }

    private void OnAuthTypeChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    // NewProfile/EditProfile/Cancel/DeleteProfileAsync synchronously re-push EditingProfile's
    // field values through these bound controls while IsAssigningEditingProfile is set — skip
    // marking a freshly-opened, unedited profile dirty from that cascade.
    private void MarkDirty()
    {
        if (ViewModel.IsAssigningEditingProfile) return;
        ViewModel.IsDirty = true;
        ViewModel.SaveProfileCommand.NotifyCanExecuteChanged();
    }
}

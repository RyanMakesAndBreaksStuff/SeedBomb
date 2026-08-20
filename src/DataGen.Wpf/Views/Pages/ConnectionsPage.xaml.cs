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
            ClientSecretBox.Password = secret;
    }

    private void OnClientSecretChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel.EditingProfile is { } profile && sender is PasswordBox box)
            profile.ClientSecret = box.Password;
        ViewModel.IsDirty = true;
        ViewModel.SaveProfileCommand.NotifyCanExecuteChanged();
    }

    private void OnProfileFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is not ConnectionManagerViewModel vm) return;
        vm.IsDirty = true;
        vm.RefreshEnvironmentUrlValidation();
        vm.SaveProfileCommand.NotifyCanExecuteChanged();
    }

    private void OnAuthTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ConnectionManagerViewModel vm) return;
        vm.IsDirty = true;
        vm.SaveProfileCommand.NotifyCanExecuteChanged();
    }
}

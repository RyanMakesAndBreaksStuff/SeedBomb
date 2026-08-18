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
    }

    /// <inheritdoc />
    public Task OnNavigatedFromAsync()
    {
        _loadCts?.Cancel();
        return Task.CompletedTask;
    }

    private void OnProfileFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is ConnectionManagerViewModel vm)
            vm.SaveProfileCommand.NotifyCanExecuteChanged();
    }
}

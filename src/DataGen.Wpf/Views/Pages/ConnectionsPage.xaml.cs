using System.Windows.Controls;
using Seedbomb.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>Connection profiles page. Footer nav destination; shares the MainWindow singleton VM.</summary>
public partial class ConnectionsPage : Page, INavigableView<ConnectionManagerViewModel>
{
    /// <inheritdoc />
    public ConnectionManagerViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    public ConnectionsPage(ConnectionManagerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }
}

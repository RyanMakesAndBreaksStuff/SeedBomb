using System.Windows;
using System.Windows.Controls;
using DataGen.Desktop.ViewModels;

namespace DataGen.Desktop.Views.Pages;

/// <summary>Displays reverse-chronological generation run history with search and CSV export.</summary>
public partial class HistoryPage : Page
{
    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public HistoryPage(HistoryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

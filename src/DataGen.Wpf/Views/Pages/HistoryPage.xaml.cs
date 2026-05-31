using System.Windows;
using System.Windows.Controls;
using DataGen.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DataGen.Wpf.Views.Pages;

/// <summary>Displays reverse-chronological generation run history with search and CSV export.</summary>
public partial class HistoryPage : Page
{
    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public HistoryPage()
    {
        DataContext = ((App)Application.Current).Services.GetRequiredService<HistoryViewModel>();
        InitializeComponent();
    }
}

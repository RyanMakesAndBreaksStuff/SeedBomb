using System.Windows;
using System.Windows.Controls;
using DataGen.Desktop.ViewModels;

namespace DataGen.Desktop.Views.Pages;

/// <summary>Settings page with Connection, Appearance, Generation Defaults, and About sections.</summary>
public partial class SettingsPage : Page
{
    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

using System.Windows;
using System.Windows.Controls;
using DataGen.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DataGen.Wpf.Views.Pages;

/// <summary>Settings page with Connection, Appearance, Generation Defaults, and About sections.</summary>
public partial class SettingsPage : Page
{
    /// <summary>Initialises the page and wires the ViewModel.</summary>
    public SettingsPage()
    {
        DataContext = ((App)Application.Current).Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }
}

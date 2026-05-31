using DataGen.Wpf.ViewModels;
using System;
using Wpf.Ui.Controls;

namespace DataGen.Wpf.Views.Windows;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel, IServiceProvider serviceProvider)
    {
        DataContext = viewModel;
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);
    }
}

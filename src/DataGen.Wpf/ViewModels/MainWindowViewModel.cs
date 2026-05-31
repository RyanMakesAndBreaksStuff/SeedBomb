using CommunityToolkit.Mvvm.ComponentModel;

namespace DataGen.Wpf.ViewModels;

/// <summary>ViewModel for <see cref="DataGen.Wpf.Views.Windows.MainWindow"/>.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _userDisplayName = "User";

    [ObservableProperty]
    private string _orgUrl = string.Empty;
}

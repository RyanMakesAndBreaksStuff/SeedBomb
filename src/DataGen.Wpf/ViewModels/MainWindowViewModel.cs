using CommunityToolkit.Mvvm.ComponentModel;

namespace DataGen.Wpf.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _userDisplayName = "User";

    [ObservableProperty]
    private string _orgUrl = string.Empty;
}

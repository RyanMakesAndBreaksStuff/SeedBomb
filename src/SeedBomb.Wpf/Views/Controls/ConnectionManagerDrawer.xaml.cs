using Seedbomb.ViewModels;
using System.Windows.Controls;

namespace Seedbomb.Views.Controls;

/// <summary>Slide-in Connection Manager drawer.</summary>
public partial class ConnectionManagerDrawer : UserControl
{
    /// <summary>Initialises the drawer.</summary>
    public ConnectionManagerDrawer()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ConnectionManagerViewModel vm)
                await vm.LoadAsync();
        };
    }

    private void OnProfileFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is ConnectionManagerViewModel vm)
            vm.SaveProfileCommand.NotifyCanExecuteChanged();
    }
}

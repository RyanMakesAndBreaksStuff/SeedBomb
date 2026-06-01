using DataGen.Desktop.ViewModels;
using System.Windows.Controls;

namespace DataGen.Desktop.Views.Controls;

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
                await vm.LoadAsync().ConfigureAwait(false);
        };
    }
}

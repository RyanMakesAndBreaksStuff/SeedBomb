using Seedbomb.ViewModels;
using Wpf.Ui.Controls;

namespace Seedbomb.Views.Dialogs;

/// <summary>
/// Profiles manager + visual import summary (Mock F5). Hosted via
/// <see cref="Wpf.Ui.IContentDialogService"/> — never renders profile JSON.
/// </summary>
public partial class ProfilesDialog : ContentDialog
{
    /// <summary>Initialises the dialog with its view-model.</summary>
    public ProfilesDialog(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Bound view-model.</summary>
    public ProfilesViewModel ViewModel => (ProfilesViewModel)DataContext;
}

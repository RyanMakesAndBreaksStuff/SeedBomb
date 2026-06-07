using CommunityToolkit.Mvvm.ComponentModel;

namespace DataGen.Desktop.ViewModels;

/// <summary>ViewModel for <see cref="DataGen.Desktop.Views.Windows.MainWindow"/>.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitials))]
    private string _userDisplayName = "User";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrgHost))]
    private string _orgUrl = string.Empty;

    /// <summary>Gets initials for the signed-in user avatar.</summary>
    public string UserInitials
    {
        get
        {
            var parts = UserDisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "U";

            return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        }
    }

    /// <summary>Gets a compact organization host name for the shell header.</summary>
    public string OrgHost =>
        Uri.TryCreate(OrgUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : OrgUrl;
}

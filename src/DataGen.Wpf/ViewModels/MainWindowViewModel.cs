using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DataGen.Desktop.ViewModels;

/// <summary>ViewModel for <see cref="DataGen.Desktop.Views.Windows.MainWindow"/>.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Raised when the header's connection display is clicked.</summary>
    public event EventHandler? OpenConnectionManagerRequested;

    /// <summary>Opens the Connection Manager drawer.</summary>
    [RelayCommand]
    private void OpenConnectionManager() =>
        OpenConnectionManagerRequested?.Invoke(this, EventArgs.Empty);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitials))]
    [NotifyPropertyChangedFor(nameof(OrgHost))]
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
            : string.IsNullOrWhiteSpace(OrgUrl)
                ? string.IsNullOrWhiteSpace(UserDisplayName) || UserDisplayName == "User" ? "Not connected" : "Connected"
                : OrgUrl;
}

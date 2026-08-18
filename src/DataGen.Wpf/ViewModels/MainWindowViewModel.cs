using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Seedbomb.ViewModels;

/// <summary>ViewModel for <see cref="Seedbomb.Views.Windows.MainWindow"/>.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Raised when the header's connection display is clicked.</summary>
    public event EventHandler? OpenConnectionManagerRequested;

    /// <summary>Raised when the sign-in overlay's button is clicked.</summary>
    public event EventHandler? SignInRequested;

    /// <summary>Opens the Connection Manager drawer.</summary>
    [RelayCommand]
    private void OpenConnectionManager() =>
        OpenConnectionManagerRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Retries sign-in for the last-used profile.</summary>
    [RelayCommand]
    private void SignIn() =>
        SignInRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Handoff alias for <see cref="OrgHost"/>.</summary>
    public string EnvironmentHost => OrgHost;

    /// <summary>Handoff alias that navigates to Connections rather than opening a drawer.</summary>
    public IRelayCommand OpenConnectionsCommand => OpenConnectionManagerCommand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInOverlay))]
    private bool _hasConnection;

    /// <summary>
    /// Set at startup when the silent sign-in attempt failed. Cleared once
    /// <see cref="Seedbomb.Views.Windows.MainWindow"/> observes a successful connection switch.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInOverlay))]
    private bool _needsSignIn;

    /// <summary>Gets whether the "Sign in to continue" overlay should be shown.</summary>
    public bool ShowSignInOverlay => HasConnection && NeedsSignIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitials))]
    [NotifyPropertyChangedFor(nameof(OrgHost))]
    [NotifyPropertyChangedFor(nameof(EnvironmentHost))]
    private string _userDisplayName = "User";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrgHost))]
    [NotifyPropertyChangedFor(nameof(EnvironmentHost))]
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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;

namespace Seedbomb.ViewModels;

/// <summary>ViewModel for <see cref="Seedbomb.Views.Windows.MainWindow"/>.</summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IConnectionProfileService? _profileService;
    private readonly ConnectionManagerViewModel? _connectionManager;
    private readonly ILogger<MainWindowViewModel>? _logger;
    private readonly SynchronizationContext? _uiContext;
    private bool _disposed;

    /// <summary>Parameterless constructor for tests that only exercise header display state.</summary>
    public MainWindowViewModel()
    {
    }

    /// <summary>DI constructor. Subscribes to profile-store and connection-switch events.</summary>
    /// <param name="profileService">Connection profile store.</param>
    /// <param name="connectionManager">Shared connection-manager view-model.</param>
    /// <param name="logger">Optional logger for the profile-reload path.</param>
    [ActivatorUtilitiesConstructor]
    public MainWindowViewModel(
        IConnectionProfileService profileService,
        ConnectionManagerViewModel connectionManager,
        ILogger<MainWindowViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        ArgumentNullException.ThrowIfNull(connectionManager);
        _profileService = profileService;
        _connectionManager = connectionManager;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        _profileService.ProfilesChanged += OnProfilesChanged;
        _connectionManager.ConnectionSwitched += HandleConnectionSwitched;
    }

    /// <summary>Raised when the header's connection display is clicked.</summary>
    public event EventHandler? OpenConnectionManagerRequested;

    /// <summary>Raised when the sign-in overlay's button is clicked.</summary>
    public event EventHandler? SignInRequested;

    /// <summary>Raised after a successful connection switch so cached pickers can reload.</summary>
    public event EventHandler? ConnectionReloadRequested;

    /// <summary>Test seam: the in-flight profile reload started by the last <see cref="OnProfilesChanged"/>.</summary>
    internal Task? PendingProfileSyncTask { get; private set; }

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
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(ConnectedOpacity))]
    private bool _hasConnection;

    /// <summary>
    /// Set at startup when the silent sign-in attempt failed. Cleared once
    /// a successful connection switch is observed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInOverlay))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(ConnectedOpacity))]
    private bool _needsSignIn;

    /// <summary>Gets whether the "Sign in to continue" overlay should be shown.</summary>
    public bool ShowSignInOverlay => HasConnection && NeedsSignIn;

    /// <summary>Gets whether the app is actually connected right now (not just "a profile exists").</summary>
    public bool IsConnected => HasConnection && !NeedsSignIn;

    /// <summary>
    /// Full opacity when connected; dimmed (not disabled) when a profile exists
    /// but nothing is actually signed in.
    /// </summary>
    public double ConnectedOpacity => IsConnected ? 1.0 : 0.55;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitials))]
    [NotifyPropertyChangedFor(nameof(OrgHost))]
    [NotifyPropertyChangedFor(nameof(EnvironmentHost))]
    private string _userDisplayName = "User";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OrgHost))] [NotifyPropertyChangedFor(nameof(EnvironmentHost))]
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
                ? string.IsNullOrWhiteSpace(UserDisplayName) || UserDisplayName == "User"
                    ? "Not connected"
                    : "Connected"
                : OrgUrl;

    /// <summary>
    /// Re-derives <see cref="HasConnection"/> from the profile count. The count is the only
    /// source of truth: deleting the last connection must re-show the first-run overlay.
    /// </summary>
    internal void SyncHasConnection() =>
        HasConnection = _connectionManager is { Profiles.Count: > 0 };

    /// <summary>
    /// Applies a successful connection switch to shell state and asks cached pickers to reload.
    /// </summary>
    /// <param name="profile">The profile that was connected.</param>
    /// <param name="result">The authentication result for that switch.</param>
    public void OnConnectionSwitched(ConnectionProfile profile, AuthResult result)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded) return;

        UserDisplayName = result.DisplayName ?? UserDisplayName;
        OrgUrl = profile.EnvironmentUrl;
        NeedsSignIn = false;
        SyncHasConnection();
        ConnectionReloadRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_profileService is not null)
            _profileService.ProfilesChanged -= OnProfilesChanged;
        if (_connectionManager is not null)
            _connectionManager.ConnectionSwitched -= HandleConnectionSwitched;
    }

    // T4: react to saves/deletes (fired by the profile store) instead of ObservableCollection's
    // CollectionChanged. LoadAsync does Profiles.Clear() then re-adds, so watching the collection
    // directly drove HasConnection false for an instant on every routine reload (flashing the
    // first-run overlay). ProfilesChanged can fire on a background thread (the store's disk write
    // uses ConfigureAwait(false) throughout) — marshal before touching the ObservableCollection.
    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            StartProfileSync();
            return;
        }

        _uiContext.Post(static s => ((MainWindowViewModel)s!).StartProfileSync(), this);
    }

    private void StartProfileSync() =>
        PendingProfileSyncTask = ReloadAndSyncHasConnectionAsync();

    private async Task ReloadAndSyncHasConnectionAsync()
    {
        try
        {
            if (_connectionManager is not null)
                await _connectionManager.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // Was a fire-and-forget discard in code-behind: an unobserved task exception that
            // DispatcherUnhandledException does not see.
            _logger?.LogError(ex, "Failed to reload connection profiles");
        }
        finally
        {
            // The profile count is the only source of truth for HasConnection: deleting the
            // last connection must re-show the first-run overlay and re-disable the nav items.
            HasConnection = _connectionManager is { Profiles.Count: > 0 };
        }
    }

    private void HandleConnectionSwitched(object? sender, (ConnectionProfile Profile, AuthResult Result) e) =>
        OnConnectionSwitched(e.Profile, e.Result);
}
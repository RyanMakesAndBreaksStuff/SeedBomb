using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace SeedBomb.ViewModels;

/// <summary>ViewModel for the Connection Manager drawer.</summary>
public sealed partial class ConnectionManagerViewModel : ObservableObject
{
    private readonly IConnectionProfileService _profileService;
    private readonly IAuthService _authService;
    private readonly IDataverseConnectionService _connectionService;
    private readonly IContentDialogService? _dialogs;
    private readonly RunViewModel? _run;
    private readonly RunSessionGate? _sessionGate;
    private readonly SynchronizationContext? _uiContext;

    /// <summary>Initialises the view-model.</summary>
    /// <param name="profileService">Connection profile store.</param>
    /// <param name="authService">Auth service.</param>
    /// <param name="connectionService">Dataverse connection cache.</param>
    /// <param name="dialogs">Optional dialog host. When null, delete proceeds unconfirmed (tests).</param>
    /// <param name="run">Optional run sheet. While it is writing, connection changes are blocked.</param>
    /// <param name="sessionGate">
    /// Process-wide run/session gate. Null (existing fixtures) does not coordinate with a live run.
    /// </param>
    public ConnectionManagerViewModel(
        IConnectionProfileService profileService,
        IAuthService authService,
        IDataverseConnectionService connectionService,
        IContentDialogService? dialogs = null,
        RunViewModel? run = null,
        RunSessionGate? sessionGate = null)
    {
        _profileService = profileService;
        _authService = authService;
        _connectionService = connectionService;
        _dialogs = dialogs;
        _run = run;
        _sessionGate = sessionGate;
        _uiContext = SynchronizationContext.Current;
        // Both singletons: the subscription lives as long as the app.
        if (_run is not null)
            _run.PropertyChanged += OnRunPropertyChanged;
        _authService.SignedOut += OnSignedOut;
    }

    // WR-001: save, delete, connect and switch can each dispose the ServiceClient a running
    // pipeline writes through, so they wait until the run ends.
    private bool RunIsWriting => _run?.IsRunning == true;

    private void OnRunPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RunViewModel.IsRunning))
            return;
        SaveProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        SelectProfileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised after a profile is selected and sign-in succeeds.</summary>
    public event EventHandler<(ConnectionProfile Profile, AuthResult Result)>? ConnectionSwitched;

    /// <summary>HWND of the hosting window, used for interactive sign-in popups. Set by the host on Loaded.</summary>
    public nint ParentHwnd { get; set; }

    [ObservableProperty] private ObservableCollection<ConnectionProfile> _profiles = [];
    [ObservableProperty] private ConnectionProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    [NotifyPropertyChangedFor(nameof(ProfileError))]
    private ConnectionProfile? _editingProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    private bool _isEditing;

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SelectProfileCommand))]
    private bool _isSwitchingConnection;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSwitchError))]
    private string? _switchError;

    /// <summary>Id of the profile the live session is signed in to. WR-003: read from the auth service, never mirrored.</summary>
    public Guid? ConnectedProfileId => _authService.ActiveProfile?.Id;

    // SignedOut can fire on a background thread (ProfileAuthService uses ConfigureAwait(false)).
    private void OnSignedOut(object? sender, EventArgs e)
    {
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
            OnPropertyChanged(nameof(ConnectedProfileId));
        else
            _uiContext.Post(static s => ((ConnectionManagerViewModel)s!).OnPropertyChanged(nameof(ConnectedProfileId)), this);
    }

    /// <summary>Whether the editing profile has unsaved edits since it was opened or last saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(ProfileError))]
    private bool _isDirty;

    /// <summary>Shown for ~2s by <see cref="ConnectAsync"/> after a successful connect.</summary>
    [ObservableProperty] private bool _showConnectedToast;

    /// <summary>Test seam: how long <see cref="ShowConnectedToast"/> stays true.</summary>
    internal TimeSpan ConnectedToastDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Test seam: the fire-and-forget toast task started by the last <see cref="ConnectAsync"/>.</summary>
    internal Task? ConnectedToastTask { get; private set; }

    /// <summary>Gets whether the last connection switch failed.</summary>
    public bool HasSwitchError => SwitchError is not null;

    private bool IsNewProfile => EditingProfile is { } p && Profiles.All(x => x.Id != p.Id);

    /// <summary>Gets whether the Save button should show: a never-saved profile, or unsaved edits.</summary>
    public bool ShowSaveButton => IsEditing && (IsNewProfile || IsDirty);

    /// <summary>Gets whether the Connect button should show: saved, with no pending edits.</summary>
    public bool ShowConnectButton => IsEditing && !IsNewProfile && !IsDirty;

    /// <summary>Gets the first sign-in problem with the edited profile, or null while it's valid or untouched.</summary>
    public string? ProfileError => EditingProfile is { } p && IsDirty ? p.SignInError() : null;

    /// <summary>Loads profiles from storage and marks the active profile.</summary>
    [RelayCommand]
    internal async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var all = await _profileService.GetAllAsync(ct);
            var lastUsed = await _profileService.GetLastUsedAsync(ct);
            ct.ThrowIfCancellationRequested();

            Profiles.Clear();
            foreach (var p in all)
            {
                p.IsLastUsed = lastUsed?.Id == p.Id;
                Profiles.Add(p);
            }

            // CR-006: say where an unreadable connections.json went instead of showing an empty list.
            if (_profileService.LoadWarning is { } warning)
                SwitchError = warning;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SwitchError = ex.Message;
        }
    }

    /// <summary>Starts creating a blank new profile with OAuth defaults pre-filled.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        SelectedProfile = null; // WR-011: so clicking the previously selected row reopens it
        EditingProfile = new ConnectionProfile
        {
            ClientId = ConnectionProfile.WellKnownClientId,
        };
        IsEditing = true;
        IsDirty = false;
    }

    /// <summary>Sets a profile as the active connection and re-authenticates against it.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectProfile))]
    private async Task SelectProfileAsync(ConnectionProfile profile)
    {
        IsSwitchingConnection = true;
        SwitchError = null;
        IDisposable? lease = null;

        try
        {
            if (_sessionGate is not null)
                lease = await _sessionGate.AcquireAsync();

            // CR-002: sign in to this profile explicitly; last-used only moves when that succeeds.
            var result = await _authService.SignInAsync(profile, ParentHwnd);

            if (result.Succeeded)
            {
                await _connectionService.ResetAsync();
                OnPropertyChanged(nameof(ConnectedProfileId));
                ConnectionSwitched?.Invoke(this, (profile, result));
            }
            else
                SwitchError = result.Error ?? "Sign-in failed.";

            await LoadAsync();
        }
        catch (Exception ex)
        {
            SwitchError = ex.Message;
        }
        finally
        {
            lease?.Dispose();
            IsSwitchingConnection = false;
        }
    }

    private bool CanSelectProfile() => !IsSwitchingConnection && !RunIsWriting;

    /// <summary>Starts editing an existing profile. Its saved secret stays encrypted.</summary>
    [RelayCommand]
    private void EditProfile(ConnectionProfile profile)
    {
        SwitchError = null;
        // IN-014: browsing connections must not decrypt secrets. A null ClientSecret saves as
        // "keep the stored one", and SignInAppOnlyAsync reads the stored one when it needs it.
        EditingProfile = new ConnectionProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            EnvironmentUrl = profile.EnvironmentUrl,
            EnvironmentType = profile.EnvironmentType,
            AuthType = profile.AuthType,
            ClientId = profile.ClientId,
            TenantId = profile.TenantId,
            HasSavedSecret = profile.HasSavedSecret,
            CertificateThumbprint = profile.CertificateThumbprint,
        };
        IsEditing = true;
        IsDirty = false;
    }

    /// <summary>Persists the editing profile. Connecting is a separate, explicit step (<see cref="ConnectAsync"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (EditingProfile is null) return;
        var savedInstance = EditingProfile;
        SwitchError = null;
        IDisposable? lease = null;
        try
        {
            if (_sessionGate is not null)
                lease = await _sessionGate.AcquireAsync();

            await _profileService.SaveAsync(savedInstance);
            await LoadAsync();

            // Guard against the user switching to a different profile (and possibly starting
            // real edits on it) while this save was still in flight — that profile's own
            // dirty state must not be clobbered by this save's completion.
            if (ReferenceEquals(EditingProfile, savedInstance))
                IsDirty = false;
        }
        catch (Exception ex)
        {
            SwitchError = ex.Message;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    // WR-012: without this an empty profile persists AND immediately triggers a doomed
    // SelectProfileAsync, so the user sees a sign-in failure rather than a validation error.
    // The AuthType credential check closes the same gap for app-only auth: a blank secret or
    // thumbprint would otherwise save and then fail sign-in with a confusing error instead of
    // being blocked at save time. SignInError is the exact check sign-in runs, so Save can't drift from it.
    private bool CanSaveProfile() =>
        !RunIsWriting
        && EditingProfile is { } p
        && !string.IsNullOrWhiteSpace(p.Name)
        && p.SignInError() is null
        && p.AuthType switch
        {
            AuthType.ClientSecret => p.HasSavedSecret || !string.IsNullOrWhiteSpace(p.ClientSecret),
            AuthType.Certificate => !string.IsNullOrWhiteSpace(p.CertificateThumbprint),
            _ => true,
        };

    /// <summary>Deletes a profile after confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfileAsync(ConnectionProfile? profile)
    {
        if (profile is null) return;
        SwitchError = null;

        try
        {
            if (_dialogs is not null
                && !await _dialogs.ConfirmAsync(
                    "Delete connection",
                    $"Delete '{profile.Name}'? Saved credentials for this connection are removed.",
                    "Delete"))
                return;

            IDisposable? lease = null;
            try
            {
                if (_sessionGate is not null)
                    lease = await _sessionGate.AcquireAsync();

                // WR-003: deleting can sign the session out (ProfilesChanged → ReconcileSessionAsync), so
                // decide on the reset from the owner before it changes.
                var wasActive = _authService.ActiveProfile?.Id == profile.Id;

                // WR-015: purge the cached MSAL refresh token before the profile record itself
                // disappears — the delete confirmation promises credential removal.
                await _authService.ForgetProfileAsync(profile);
                await _profileService.DeleteAsync(profile.Id);
                await LoadAsync();

                // Delete connection's CommandParameter is EditingProfile itself, so a successful
                // delete always empties the pane it was just deleted from — otherwise the stale
                // profile stays visible/editable after it no longer exists in the store.
                if (EditingProfile?.Id == profile.Id)
                {
                    EditingProfile = null;
                    IsEditing = false;
                }

                // Re-check under the lease. Do not wait for fire-and-forget reconciliation to drop
                // a deleted active session, and do not sign out a profile that became active while
                // the confirmation was up.
                var activeId = _authService.ActiveProfile?.Id;
                if (activeId == profile.Id)
                    await _authService.SignOutAsync();
                if (wasActive || activeId == profile.Id)
                {
                    OnPropertyChanged(nameof(ConnectedProfileId));
                    // WR-001: only deleting the connected profile drops the live connection; saving or
                    // deleting any other profile leaves it alone.
                    await _connectionService.ResetAsync();
                }
            }
            finally
            {
                lease?.Dispose();
            }
        }
        catch (Exception ex)
        {
            // No dialog host (e.g. login window before MainWindow loads) must not delete unconfirmed.
            SwitchError = ex.Message;
        }
    }

    private bool CanDeleteProfile() => !RunIsWriting;

    /// <summary>Tests the connection for the current editing profile.</summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (EditingProfile is null) return;
        IsTesting = true;
        TestResult = null;

        try
        {
            var result = await _authService.TryConnectAsync(EditingProfile, ParentHwnd);
            TestSucceeded = result.Succeeded;
            TestResult = result.Succeeded ? $"Connected as {result.DisplayName}" : result.Error ?? "Connection failed.";
        }
        catch (Exception ex)
        {
            TestSucceeded = false;
            TestResult = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Discards edits without saving.</summary>
    [RelayCommand]
    private void Cancel()
    {
        SelectedProfile = null; // WR-011
        IsEditing = false;
        EditingProfile = null;
        TestResult = null;
        IsDirty = false;
    }

    /// <summary>Connects to the saved, clean editing profile. Separate from Save — see <see cref="ShowConnectButton"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (EditingProfile is null) return;
        var saved = Profiles.FirstOrDefault(p => p.Id == EditingProfile.Id);
        if (saved is null) return;

        await SelectProfileAsync(saved);

        // Fire-and-forget: the toast's display time shouldn't hold ConnectCommand busy after the
        // real work above has already finished. Task.Delay can't throw here (no cancellation
        // token passed), so there's no unobserved-exception risk.
        if (SwitchError is null)
            ConnectedToastTask = ShowConnectedToastAsync();
    }

    private async Task ShowConnectedToastAsync()
    {
        ShowConnectedToast = true;
        await Task.Delay(ConnectedToastDuration);
        ShowConnectedToast = false;
    }

    private bool CanConnect() => ShowConnectButton && !RunIsWriting;

    /// <summary>Writes Microsoft's well-known public client ID into the editing profile.</summary>
    [RelayCommand]
    private void UseDefaultClientId()
    {
        if (EditingProfile is null) return;
        EditingProfile.ClientId = ConnectionProfile.WellKnownClientId;
    }

    partial void OnEditingProfileChanging(ConnectionProfile? oldValue, ConnectionProfile? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnEditingProfileFieldChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnEditingProfileFieldChanged;
    }

    private void OnEditingProfileFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        IsDirty = true;
        SaveProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ProfileError));
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value is not null)
            EditProfile(value);
    }
}

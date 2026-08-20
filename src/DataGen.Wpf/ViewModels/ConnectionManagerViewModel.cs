using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Dataverse;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace Seedbomb.ViewModels;

/// <summary>ViewModel for the Connection Manager drawer.</summary>
public sealed partial class ConnectionManagerViewModel : ObservableObject
{
    private readonly IConnectionProfileService _profileService;
    private readonly IAuthService _authService;
    private readonly IDataverseConnectionService _connectionService;
    private readonly IContentDialogService? _dialogs;

    /// <summary>Initialises the view-model.</summary>
    /// <param name="profileService">Connection profile store.</param>
    /// <param name="authService">Auth service.</param>
    /// <param name="connectionService">Dataverse connection cache.</param>
    /// <param name="dialogs">Optional dialog host. When null, delete proceeds unconfirmed (tests).</param>
    public ConnectionManagerViewModel(
        IConnectionProfileService profileService,
        IAuthService authService,
        IDataverseConnectionService connectionService,
        IContentDialogService? dialogs = null)
    {
        _profileService = profileService;
        _authService = authService;
        _connectionService = connectionService;
        _dialogs = dialogs;
    }

    /// <summary>Raised when the drawer should close.</summary>
    public event EventHandler? DrawerCloseRequested;

    /// <summary>Raised after a profile is selected and sign-in succeeds.</summary>
    public event EventHandler<(ConnectionProfile Profile, AuthResult Result)>? ConnectionSwitched;

    /// <summary>HWND of the hosting window, used for interactive sign-in popups. Set by the host on Loaded.</summary>
    public nint ParentHwnd { get; set; }

    [ObservableProperty] private ObservableCollection<ConnectionProfile> _profiles = [];
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    private ConnectionProfile? _editingProfile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    private bool _isEditing;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SelectProfileCommand))] private bool _isSwitchingConnection;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSwitchError))] private string? _switchError;

    /// <summary>Not persisted. Id of the profile the app is actually connected to right now, if any.</summary>
    [ObservableProperty] private Guid? _connectedProfileId;

    /// <summary>Whether the editing profile has unsaved edits since it was opened or last saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isDirty;

    /// <summary>Shown for ~2s by <see cref="ConnectAsync"/> after a successful connect.</summary>
    [ObservableProperty] private bool _showConnectedToast;

    /// <summary>Test seam: how long <see cref="ShowConnectedToast"/> stays true.</summary>
    internal TimeSpan ConnectedToastDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets whether the last connection switch failed.</summary>
    public bool HasSwitchError => SwitchError is not null;

    /// <summary>Handoff alias for <see cref="CancelCommand"/>.</summary>
    public IRelayCommand CancelEditCommand => CancelCommand;

    private bool IsNewProfile => EditingProfile is { } p && Profiles.All(x => x.Id != p.Id);

    /// <summary>Gets whether the Save button should show: a never-saved profile, or unsaved edits.</summary>
    public bool ShowSaveButton => IsEditing && (IsNewProfile || IsDirty);

    /// <summary>Gets whether the Connect button should show: saved, with no pending edits.</summary>
    public bool ShowConnectButton => IsEditing && !IsNewProfile && !IsDirty;

    /// <summary>Gets an inline validation message for the environment URL, or null when it's valid or blank.</summary>
    public string? EnvironmentUrlError =>
        EditingProfile is { } p && !string.IsNullOrWhiteSpace(p.EnvironmentUrl)
            && !(Uri.TryCreate(p.EnvironmentUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            ? "Enter a valid https:// environment URL, e.g. https://contoso.crm.dynamics.com"
            : null;

    /// <summary>Re-evaluates <see cref="EnvironmentUrlError"/>. EnvironmentUrl's setter doesn't raise PropertyChanged.</summary>
    internal void RefreshEnvironmentUrlValidation() => OnPropertyChanged(nameof(EnvironmentUrlError));

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
        EditingProfile = new ConnectionProfile
        {
            // Microsoft's well-known public client ID for Dynamics 365 / Power Platform
            ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
        };
        IsEditing = true;

        // Must run last: assigning EditingProfile above synchronously re-pushes its (blank)
        // field values through the bound TextBoxes, which raises TextChanged and marks the
        // fresh, unedited profile dirty via OnProfileFieldChanged before this line runs.
        IsDirty = false;
    }

    /// <summary>Sets a profile as the active connection and re-authenticates against it.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectProfile))]
    private async Task SelectProfileAsync(ConnectionProfile profile)
    {
        IsSwitchingConnection = true;
        SwitchError = null;

        try
        {
            await _profileService.SetLastUsedAsync(profile.Id);
            var result = await _authService.SignInAsync(ParentHwnd);

            if (result.Succeeded)
            {
                _connectionService.Reset();
                ConnectedProfileId = profile.Id;
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
            IsSwitchingConnection = false;
        }
    }

    private bool CanSelectProfile() => !IsSwitchingConnection;

    /// <summary>Starts editing an existing profile.</summary>
    [RelayCommand]
    private void EditProfile(ConnectionProfile profile)
    {
        EditingProfile = new ConnectionProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            EnvironmentUrl = profile.EnvironmentUrl,
            EnvironmentType = profile.EnvironmentType,
            AuthType = profile.AuthType,
            ClientId = profile.ClientId,
            TenantId = profile.TenantId,
            ClientSecret = profile.ClientSecret,
            CertificateThumbprint = profile.CertificateThumbprint,
        };
        IsEditing = true;

        // Same ordering reason as NewProfile: must run last, after the bound TextBoxes have
        // already re-pushed the cloned profile's values and spuriously marked it dirty.
        IsDirty = false;
    }

    /// <summary>Persists the editing profile. Connecting is a separate, explicit step (<see cref="ConnectAsync"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (EditingProfile is null) return;
        SwitchError = null;
        try
        {
            await _profileService.SaveAsync(EditingProfile);
            await LoadAsync();
            IsDirty = false;
        }
        catch (Exception ex)
        {
            SwitchError = ex.Message;
        }
    }

    // WR-012: without this an empty profile persists AND immediately triggers a doomed
    // SelectProfileAsync, so the user sees a sign-in failure rather than a validation error.
    // The AuthType credential check closes the same gap for app-only auth: a blank secret or
    // thumbprint would otherwise save and then fail sign-in with a confusing error instead of
    // being blocked at save time.
    private bool CanSaveProfile() =>
        EditingProfile is { } p
        && !string.IsNullOrWhiteSpace(p.Name)
        && !string.IsNullOrWhiteSpace(p.ClientId)
        && Uri.TryCreate(p.EnvironmentUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && p.AuthType switch
        {
            AuthType.ClientSecret => !string.IsNullOrWhiteSpace(p.ClientSecret),
            AuthType.Certificate => !string.IsNullOrWhiteSpace(p.CertificateThumbprint),
            _ => true,
        };

    /// <summary>Deletes a profile after confirmation.</summary>
    [RelayCommand]
    private async Task DeleteProfileAsync(ConnectionProfile? profile)
    {
        if (profile is null) return;
        SwitchError = null;

        try
        {
            if (_dialogs is not null)
            {
                var choice = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
                {
                    Title = "Delete connection",
                    Content = $"Delete '{profile.Name}'? Saved credentials for this connection are removed.",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                });

                if (choice != ContentDialogResult.Primary)
                    return;
            }

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

            if (ConnectedProfileId == profile.Id)
                ConnectedProfileId = null;
        }
        catch (Exception ex)
        {
            // No dialog host (e.g. login window before MainWindow loads) must not delete unconfirmed.
            SwitchError = ex.Message;
        }
    }

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

        if (SwitchError is null)
        {
            ShowConnectedToast = true;
            await Task.Delay(ConnectedToastDuration);
            ShowConnectedToast = false;
        }
    }

    private bool CanConnect() => ShowConnectButton;

    /// <summary>Writes Microsoft's well-known public client ID into the editing profile.</summary>
    [RelayCommand]
    private void UseDefaultClientId()
    {
        if (EditingProfile is null) return;
        EditingProfile.ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        IsDirty = true;
        OnPropertyChanged(nameof(EditingProfile));
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Requests the drawer to close.</summary>
    [RelayCommand]
    private void Close() => DrawerCloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value is not null)
            EditProfile(value);
    }
}

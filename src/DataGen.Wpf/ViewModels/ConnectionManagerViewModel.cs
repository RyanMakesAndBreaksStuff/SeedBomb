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
    private ConnectionProfile? _editingProfile;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SelectProfileCommand))] private bool _isSwitchingConnection;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSwitchError))] private string? _switchError;

    /// <summary>Gets whether the last connection switch failed.</summary>
    public bool HasSwitchError => SwitchError is not null;

    /// <summary>Handoff alias for <see cref="CancelCommand"/>.</summary>
    public IRelayCommand CancelEditCommand => CancelCommand;

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
            Username = profile.Username,
            Password = profile.Password,
        };
        IsEditing = true;
    }

    /// <summary>Persists the editing profile and exits edit mode.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (EditingProfile is null) return;
        SwitchError = null;
        try
        {
            var savedInstance = EditingProfile;
            await _profileService.SaveAsync(savedInstance);
            await LoadAsync();
            if (ReferenceEquals(EditingProfile, savedInstance))
            {
                var saved = Profiles.FirstOrDefault(p => p.Id == savedInstance.Id);
                if (saved is not null)
                    await SelectProfileAsync(saved);
            }
            IsEditing = false;
            EditingProfile = null;
        }
        catch (Exception ex)
        {
            SwitchError = ex.Message;
        }
    }

    // WR-012: without this an empty profile persists AND immediately triggers a doomed
    // SelectProfileAsync, so the user sees a sign-in failure rather than a validation error.
    private bool CanSaveProfile() =>
        EditingProfile is { } p
        && !string.IsNullOrWhiteSpace(p.Name)
        && !string.IsNullOrWhiteSpace(p.ClientId)
        && Uri.TryCreate(p.EnvironmentUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Deletes a profile after confirmation.</summary>
    [RelayCommand]
    private async Task DeleteProfileAsync(ConnectionProfile? profile)
    {
        if (profile is null) return;
        SwitchError = null;

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

        try
        {
            await _profileService.DeleteAsync(profile.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
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
    }

    /// <summary>Writes Microsoft's well-known public client ID into the editing profile.</summary>
    [RelayCommand]
    private void UseDefaultClientId()
    {
        if (EditingProfile is null) return;
        EditingProfile.ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        OnPropertyChanged(nameof(EditingProfile));
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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Dataverse;

namespace Seedbomb.ViewModels;

/// <summary>ViewModel for the Connection Manager drawer.</summary>
public sealed partial class ConnectionManagerViewModel : ObservableObject
{
    private readonly IConnectionProfileService _profileService;
    private readonly IAuthService _authService;
    private readonly IDataverseConnectionService _connectionService;

    /// <summary>Initialises the view-model.</summary>
    public ConnectionManagerViewModel(
        IConnectionProfileService profileService,
        IAuthService authService,
        IDataverseConnectionService connectionService)
    {
        _profileService = profileService;
        _authService = authService;
        _connectionService = connectionService;
    }

    /// <summary>Raised when the drawer should close.</summary>
    public event EventHandler? DrawerCloseRequested;

    /// <summary>Raised after a profile is selected and sign-in succeeds.</summary>
    public event EventHandler<(ConnectionProfile Profile, AuthResult Result)>? ConnectionSwitched;

    /// <summary>HWND of the hosting window, used for interactive sign-in popups. Set by the host on Loaded.</summary>
    public nint ParentHwnd { get; set; }

    [ObservableProperty] private ObservableCollection<ConnectionProfile> _profiles = [];
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private ConnectionProfile? _editingProfile;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SelectProfileCommand))] private bool _isSwitchingConnection;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSwitchError))] private string? _switchError;

    /// <summary>Gets whether the last connection switch failed.</summary>
    public bool HasSwitchError => SwitchError is not null;

    /// <summary>Loads profiles from storage and marks the active profile.</summary>
    [RelayCommand]
    internal async Task LoadAsync()
    {
        try
        {
            var all = await _profileService.GetAllAsync();
            var lastUsed = await _profileService.GetLastUsedAsync();

            Profiles.Clear();
            foreach (var p in all)
            {
                p.IsLastUsed = lastUsed?.Id == p.Id;
                Profiles.Add(p);
            }
        }
        catch (Exception ex)
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
    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (EditingProfile is null) return;
        SwitchError = null;
        try
        {
            await _profileService.SaveAsync(EditingProfile);
            await LoadAsync();
            IsEditing = false;
            EditingProfile = null;
        }
        catch (Exception ex)
        {
            SwitchError = ex.Message;
        }
    }

    /// <summary>Deletes a profile.</summary>
    [RelayCommand]
    private async Task DeleteProfileAsync(ConnectionProfile profile)
    {
        SwitchError = null;
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

    /// <summary>Requests the drawer to close.</summary>
    [RelayCommand]
    private void Close() => DrawerCloseRequested?.Invoke(this, EventArgs.Empty);
}

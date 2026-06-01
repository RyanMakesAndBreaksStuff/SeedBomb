using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Desktop.Services.Auth;
using DataGen.Desktop.Services.Connections;

namespace DataGen.Desktop.ViewModels;

/// <summary>ViewModel for the Connection Manager drawer.</summary>
public sealed partial class ConnectionManagerViewModel : ObservableObject
{
    private readonly IConnectionProfileService _profileService;
    private readonly IAuthService _authService;

    /// <summary>Initialises the view-model.</summary>
    public ConnectionManagerViewModel(IConnectionProfileService profileService, IAuthService authService)
    {
        _profileService = profileService;
        _authService = authService;
    }

    /// <summary>Raised when the drawer should close.</summary>
    public event EventHandler? DrawerCloseRequested;

    [ObservableProperty] private ObservableCollection<ConnectionProfile> _profiles = [];
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    [ObservableProperty] private ConnectionProfile? _editingProfile;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;

    /// <summary>Loads profiles from storage and marks the active profile.</summary>
    [RelayCommand]
    internal async Task LoadAsync()
    {
        var all = await _profileService.GetAllAsync().ConfigureAwait(false);
        var lastUsed = await _profileService.GetLastUsedAsync().ConfigureAwait(false);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Profiles.Clear();
            foreach (var p in all)
            {
                p.IsLastUsed = lastUsed?.Id == p.Id;
                Profiles.Add(p);
            }
        });
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

    /// <summary>Sets a profile as the active connection for sign-in.</summary>
    [RelayCommand]
    private async Task SelectProfileAsync(ConnectionProfile profile)
    {
        await _profileService.SetLastUsedAsync(profile.Id).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

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
        await _profileService.SaveAsync(EditingProfile).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
        IsEditing = false;
        EditingProfile = null;
    }

    /// <summary>Deletes a profile.</summary>
    [RelayCommand]
    private async Task DeleteProfileAsync(ConnectionProfile profile)
    {
        await _profileService.DeleteAsync(profile.Id).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
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
            // Save temporarily so the auth service can pick it up
            await _profileService.SaveAsync(EditingProfile).ConfigureAwait(false);
            await _profileService.SetLastUsedAsync(EditingProfile.Id).ConfigureAwait(false);

            var result = await _authService.SignInAsync(nint.Zero).ConfigureAwait(false);
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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Desktop.Services.Auth;
using DataGen.Desktop.Services.Connections;

namespace DataGen.Desktop.ViewModels;

/// <summary>ViewModel for <see cref="DataGen.Desktop.Views.Windows.LoginWindow"/>.</summary>
public sealed partial class LoginWindowViewModel : ObservableObject
{
    private readonly IAuthService _authService;
    private readonly IConnectionProfileService _profileService;

    /// <summary>Initialises the view-model.</summary>
    public LoginWindowViewModel(IAuthService authService, IConnectionProfileService profileService)
    {
        _authService = authService;
        _profileService = profileService;
        _profileService.ProfilesChanged += OnProfilesChanged;
        LoadActiveProfileAsync();
    }

    /// <summary>Raised when MSAL sign-in succeeds; arg is the user's display name.</summary>
    public event EventHandler<string>? LoginSucceeded;

    /// <summary>Raised when the Connection Manager drawer should open.</summary>
    public event EventHandler? OpenConnectionManagerRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfiles), nameof(HasNoProfiles), nameof(SignInButtonText))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private ConnectionProfile? _activeProfile;

    /// <summary>Gets a value indicating whether an error message is present.</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>Gets whether at least one connection profile exists.</summary>
    public bool HasProfiles => ActiveProfile is not null;

    /// <summary>Gets whether no connection profiles exist.</summary>
    public bool HasNoProfiles => ActiveProfile is null;

    /// <summary>Gets the sign-in button text — changes based on active profile.</summary>
    public string SignInButtonText =>
        ActiveProfile is null ? "Sign In" : $"Sign In Using {ActiveProfile.Name}";

    /// <summary>Opens the Connection Manager drawer.</summary>
    [RelayCommand]
    private void OpenConnectionManager() =>
        OpenConnectionManagerRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Initiates sign-in. <paramref name="parameter"/> must be the parent window HWND
    /// cast to <see cref="nint"/>, obtained via <see cref="System.Windows.Interop.WindowInteropHelper"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync(object? parameter)
    {
        var parentHwnd = parameter is nint hwnd ? hwnd : nint.Zero;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var result = await _authService.SignInAsync(parentHwnd);
            if (result.Succeeded)
                LoginSucceeded?.Invoke(this, result.DisplayName ?? string.Empty);
            else
                ErrorMessage = result.Error ?? "Sign-in failed.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanLogin() => !IsLoading && HasProfiles;

    private void LoadActiveProfileAsync()
    {
        _ = Task.Run(async () =>
        {
            var profile = await _profileService.GetLastUsedAsync().ConfigureAwait(false);
            System.Windows.Application.Current.Dispatcher.Invoke(() => ActiveProfile = profile);
        });
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => LoadActiveProfileAsync();
}

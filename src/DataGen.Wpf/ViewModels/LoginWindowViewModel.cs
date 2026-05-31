using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Wpf.Services.Auth;

namespace DataGen.Wpf.ViewModels;

/// <summary>ViewModel for <see cref="DataGen.Wpf.Views.Windows.LoginWindow"/>.</summary>
public sealed partial class LoginWindowViewModel : ObservableObject
{
    private readonly IAuthService _authService;

    /// <summary>Initialises the view-model.</summary>
    /// <param name="authService">Auth service used to sign in.</param>
    public LoginWindowViewModel(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>Raised when MSAL sign-in succeeds; arg is the user's display name.</summary>
    public event EventHandler<string>? LoginSucceeded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isLoading;

    /// <summary>Gets a value indicating whether an error message is present.</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// Initiates MSAL sign-in. <paramref name="parameter"/> must be the parent window HWND
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

    private bool CanLogin() => !IsLoading;
}

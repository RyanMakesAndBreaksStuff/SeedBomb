using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Dataverse;
using Seedbomb.Services.Settings;
using Seedbomb.Services.Theme;
using Seedbomb.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Seedbomb.ViewModels;

/// <summary>ViewModel for the Settings page.</summary>
/// <remarks>Initialises the view-model.</remarks>
/// <param name="settingsService">Settings persistence service.</param>
/// <param name="logger">Logger.</param>
/// <param name="snackbar">Optional snackbar for I/O failures. Tests keep the 2-arg ctor.</param>
/// <param name="auth">Optional auth service for sign-out. Tests may omit it.</param>
/// <param name="connections">Optional Dataverse connection cache to reset on sign-out.</param>
public sealed partial class SettingsViewModel(
    ISettingsService settingsService,
    ILogger<SettingsViewModel> logger,
    ISnackbarService? snackbar = null,
    IAuthService? auth = null,
    IDataverseConnectionService? connections = null) : ViewModelBase
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly ILogger<SettingsViewModel> _logger = logger;
    private readonly ISnackbarService? _snackbar = snackbar;
    private readonly IAuthService? _auth = auth;
    private readonly IDataverseConnectionService? _connections = connections;
    private AppSettings _loadedSettings = AppSettings.Default;
    private bool _isLoadingSettings;
    private CancellationTokenSource? _appearanceSaveCts;
    private CancellationTokenSource? _navCts;
    private Task _appearanceSaveTask = Task.CompletedTask;
    [ObservableProperty] private int _defaultRecordCount = 10;
    [ObservableProperty] private int _defaultBatchSize = 500;
    [ObservableProperty] private int _defaultDop;
    [ObservableProperty] private bool _darkTheme;
    [ObservableProperty] private bool _reduceMotion;
    [ObservableProperty] private string _paletteId = DesignThemeManager.DefaultPaletteId;

    /// <summary>Gets the application version string.</summary>
    public string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Gets the .NET runtime version string.</summary>
    public string DotnetVersion => $".NET {Environment.Version}";

    /// <summary>Gets the palettes offered in the appearance picker.</summary>
    public IReadOnlyList<ThemePaletteOption> AvailablePalettes => DesignThemeManager.AvailablePalettes;

    // Live dark-theme toggle - no save required; takes effect immediately.
    partial void OnDarkThemeChanged(bool value)
    {
        DesignThemeManager.Apply(value, PaletteId);

        if (!_isLoadingSettings)
            QueueAppearanceSave();
    }

    partial void OnReduceMotionChanged(bool value)
    {
        DesignThemeManager.ReduceMotion = value;
        if (!_isLoadingSettings)
            QueueAppearanceSave();
    }

    // Live palette picker - no save required; takes effect immediately, mirrors dark-theme toggle.
    partial void OnPaletteIdChanged(string value)
    {
        DesignThemeManager.Apply(DarkTheme, value);

        if (!_isLoadingSettings)
            QueueAppearanceSave();
    }

    /// <inheritdoc />
    public override Task OnNavigatedToAsync()
    {
        _navCts?.Cancel();
        _navCts?.Dispose();
        _navCts = new CancellationTokenSource();
        return LoadAsync(_navCts.Token);
    }

    /// <inheritdoc />
    public override async Task OnNavigatedFromAsync()
    {
        _navCts?.Cancel();
        await _appearanceSaveTask;
    }

    [RelayCommand]
    private void SelectPalette(string? paletteId)
    {
        if (string.IsNullOrWhiteSpace(paletteId))
            return;
        PaletteId = DesignThemeManager.ResolvePaletteId(paletteId);
    }

    [RelayCommand]
    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            _isLoadingSettings = true;
            var s = await _settingsService.LoadAsync(ct);
            _loadedSettings = s;
            DefaultRecordCount = s.DefaultRecordCount;
            DefaultBatchSize = s.DefaultBatchSize;
            DefaultDop = s.DefaultDop;
            DarkTheme = s.DarkTheme;
            ReduceMotion = s.ReduceMotion;
            PaletteId = DesignThemeManager.ResolvePaletteId(s.PaletteId);
        }
        catch (OperationCanceledException)
        {
            // Navigation cancelled the load.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var settings = new AppSettings(
                _loadedSettings.OrgUrl,
                _loadedSettings.ClientId,
                _loadedSettings.TenantId,
                DefaultRecordCount, DefaultBatchSize, DefaultDop,
                DarkTheme, ReduceMotion, PaletteId, _loadedSettings.KeepRunSheetOpen);

            CancelPendingAppearanceSave();
            await _settingsService.SaveAsync(settings);
            _loadedSettings = settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            _snackbar?.Show("Settings not saved", ex.Message,
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>Signs out of the current session and drops the cached Dataverse connection.</summary>
    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (_auth is null) return;
        try
        {
            await _auth.SignOutAsync();
            _connections?.Reset();
            _snackbar?.Show("Signed out", "Sign in again to reconnect.",
                ControlAppearance.Success, null, TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign out failed");
            _snackbar?.Show("Sign out failed", ex.Message,
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    private void QueueAppearanceSave()
    {
        CancelPendingAppearanceSave();
        _appearanceSaveCts = new CancellationTokenSource();
        _appearanceSaveTask = SaveAppearanceAsync(_appearanceSaveCts.Token);
    }

    private void CancelPendingAppearanceSave()
    {
        _appearanceSaveCts?.Cancel();
        _appearanceSaveCts?.Dispose();
        _appearanceSaveCts = null;
    }

    private async Task SaveAppearanceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);

            var settings = _loadedSettings with
            {
                DarkTheme = DarkTheme,
                ReduceMotion = ReduceMotion,
                PaletteId = PaletteId,
            };

            await _settingsService.SaveAsync(settings, ct);
            _loadedSettings = settings;
        }
        catch (OperationCanceledException)
        {
            // A newer appearance change superseded this save.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save appearance settings");
            _snackbar?.Show("Settings not saved", ex.Message,
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.Services.Settings;
using SeedBomb.Services.Theme;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace SeedBomb.ViewModels;

/// <summary>ViewModel for the Settings page.</summary>
/// <remarks>Initialises the view-model.</remarks>
/// <param name="settingsService">Settings persistence service.</param>
/// <param name="logger">Logger.</param>
/// <param name="snackbar">Optional snackbar for I/O failures. Tests keep the 2-arg ctor.</param>
/// <param name="auth">Optional auth service for sign-out. Tests may omit it.</param>
/// <param name="connections">Optional Dataverse connection cache to reset on sign-out.</param>
/// <param name="profiles">Optional connection profile store, used to tailor the sign-out message to the active profile's auth type.</param>
/// <param name="run">Optional run sheet. While it is writing, sign-out is blocked.</param>
public sealed partial class SettingsViewModel(
    ISettingsService settingsService,
    ILogger<SettingsViewModel> logger,
    ISnackbarService? snackbar = null,
    IAuthService? auth = null,
    IDataverseConnectionService? connections = null,
    IConnectionProfileService? profiles = null,
    RunViewModel? run = null) : ViewModelBase
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly ILogger<SettingsViewModel> _logger = logger;
    private readonly ISnackbarService? _snackbar = snackbar;
    private readonly IAuthService? _auth = auth;
    private readonly IDataverseConnectionService? _connections = connections;
    private readonly IConnectionProfileService? _profiles = profiles;
    private readonly RunViewModel? _run = run;
    private AppSettings _loadedSettings = AppSettings.Default;
    private bool _isLoadingSettings;
    private bool _loadFailed;
    private CancellationTokenSource? _appearanceSaveCts;
    private CancellationTokenSource? _navCts;
    private Task _appearanceSaveTask = Task.CompletedTask;
    [ObservableProperty] private int _defaultRecordCount = 10;
    [ObservableProperty] private int _defaultBatchSize = 500;
    [ObservableProperty] private int _defaultDop;
    [ObservableProperty] private bool _darkTheme;
    [ObservableProperty] private string _paletteId = DesignThemeManager.DefaultPaletteId;
    [ObservableProperty] private bool _keepRunSheetOpen = true;

    /// <summary>Gets the palettes offered in the appearance picker.</summary>
    public IReadOnlyList<ThemePaletteOption> AvailablePalettes => DesignThemeManager.AvailablePalettes;

    // Live dark-theme toggle - no save required; takes effect immediately.
    partial void OnDarkThemeChanged(bool value)
    {
        DesignThemeManager.Apply(value, PaletteId);

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
        // Transient page VM: subscribe only while shown so the singleton run never pins it.
        if (_run is not null)
        {
            _run.PropertyChanged -= OnRunPropertyChanged;
            _run.PropertyChanged += OnRunPropertyChanged;
        }

        _navCts?.Cancel();
        _navCts?.Dispose();
        _navCts = new CancellationTokenSource();
        return LoadAsync(_navCts.Token);
    }

    /// <inheritdoc />
    public override async Task OnNavigatedFromAsync()
    {
        if (_run is not null)
            _run.PropertyChanged -= OnRunPropertyChanged;
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
            PaletteId = DesignThemeManager.ResolvePaletteId(s.PaletteId);
            KeepRunSheetOpen = s.KeepRunSheetOpen;

            // A retried load after an earlier failure must re-enable Save.
            if (_loadFailed)
            {
                _loadFailed = false;
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Navigation cancelled the load.
        }
        catch (Exception ex)
        {
            // WR-004: _loadedSettings stays at AppSettings.Default here. Without this guard, any
            // appearance toggle (OnDarkThemeChanged/OnPaletteIdChanged) or Save would persist
            // AppSettings.Default over the user's real record count, batch size, DOP and
            // KeepRunSheetOpen.
            _logger.LogError(ex, "Failed to load settings");
            _loadFailed = true;
            _snackbar?.Show("Settings not loaded", ex.Message,
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
            SaveCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private bool CanSave() => !_loadFailed;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        try
        {
            var settings = new AppSettings(
                DefaultRecordCount, DefaultBatchSize, Math.Clamp(DefaultDop, 0, GenerateViewModel.MaxDop),
                DarkTheme, PaletteId, KeepRunSheetOpen);

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

    // WR-001: sign-out disposes the ServiceClient a running pipeline writes through.
    private bool CanSignOut() => _run is not { IsRunning: true };

    private void OnRunPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunViewModel.IsRunning))
            SignOutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Signs out of the current session and drops the cached Dataverse connection.</summary>
    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOutAsync()
    {
        if (_auth is null) return;
        try
        {
            await _auth.SignOutAsync();
            if (_connections is not null)
                await _connections.ResetAsync();

            // WR-T7: app-only profiles (client secret / certificate) have no user account to sign
            // out of - the credential stays on disk and the profile stays last-used, so the next
            // Dataverse call reconnects automatically. Reflect that in the message instead of
            // implying a fresh sign-in is required.
            var profile = _profiles is null ? null : await _profiles.GetLastUsedAsync();
            var appOnly = profile?.AuthType is AuthType.ClientSecret or AuthType.Certificate;

            _snackbar?.Show(
                appOnly ? "Session cleared" : "Signed out",
                appOnly
                    ? "This connection signs in with an application credential, so it reconnects automatically on next launch. Remove the credential on the Connections page to stop that."
                    : "Sign in again to reconnect.",
                ControlAppearance.Success, null, TimeSpan.FromSeconds(appOnly ? 10 : 6));
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
        if (_loadFailed)
            return;

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
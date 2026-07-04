using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Desktop.Services.Settings;
using DataGen.Desktop.Services.Theme;
using Microsoft.Extensions.Logging;

namespace DataGen.Desktop.ViewModels;

/// <summary>ViewModel for the Settings page.</summary>
/// <remarks>Initialises the view-model.</remarks>
/// <param name="settingsService">Settings persistence service.</param>
/// <param name="logger">Logger.</param>
public sealed partial class SettingsViewModel(
    ISettingsService settingsService,
    ILogger<SettingsViewModel> logger) : ViewModelBase
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly ILogger<SettingsViewModel> _logger = logger;
    private AppSettings _loadedSettings = AppSettings.Default;
    private bool _isLoadingSettings;
    private CancellationTokenSource? _appearanceSaveCts;
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
        LoadCommand.Execute(null);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task OnNavigatedFromAsync() =>
        await _appearanceSaveTask;

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            _isLoadingSettings = true;
            var s = await _settingsService.LoadAsync();
            _loadedSettings = s;
            DefaultRecordCount = s.DefaultRecordCount;
            DefaultBatchSize = s.DefaultBatchSize;
            DefaultDop = s.DefaultDop;
            DarkTheme = s.DarkTheme;
            ReduceMotion = s.ReduceMotion;
            PaletteId = s.PaletteId;
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
                DarkTheme, ReduceMotion, PaletteId);

            CancelPendingAppearanceSave();
            await _settingsService.SaveAsync(settings);
            _loadedSettings = settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
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
        }
    }
}

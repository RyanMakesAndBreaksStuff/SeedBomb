using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Wpf.Services.Dataverse;
using DataGen.Wpf.Services.Settings;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;

namespace DataGen.Wpf.ViewModels;

/// <summary>ViewModel for the Settings page.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IDataverseConnectionService _connectionService;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>Initialises the view-model.</summary>
    /// <param name="settingsService">Settings persistence service.</param>
    /// <param name="connectionService">Dataverse connection service (reset on org URL change).</param>
    /// <param name="logger">Logger.</param>
    public SettingsViewModel(
        ISettingsService settingsService,
        IDataverseConnectionService connectionService,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _connectionService = connectionService;
        _logger = logger;
    }

    [ObservableProperty] private string _orgUrl = string.Empty;
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private string _tenantId = string.Empty;
    [ObservableProperty] private int _defaultRecordCount = 10;
    [ObservableProperty] private int _defaultBatchSize = 500;
    [ObservableProperty] private int _defaultDop;
    [ObservableProperty] private bool _darkTheme;
    [ObservableProperty] private bool _reduceMotion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string? _connectionTestResult;

    [ObservableProperty] private bool _testConnectionSucceeded;

    /// <summary>Gets a value indicating whether a connection test result is available.</summary>
    public bool HasTestResult => ConnectionTestResult is not null;

    /// <summary>Gets the application version string.</summary>
    public string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Gets the .NET runtime version string.</summary>
    public string DotnetVersion => $".NET {Environment.Version}";

    // Live dark-theme toggle — no save required; takes effect immediately.
    partial void OnDarkThemeChanged(bool value) =>
        ApplicationThemeManager.Apply(value ? ApplicationTheme.Dark : ApplicationTheme.Light);

    /// <inheritdoc />
    public override void OnNavigatedTo() => LoadCommand.Execute(null);

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var s = await _settingsService.LoadAsync();
            OrgUrl = s.OrgUrl;
            ClientId = s.ClientId;
            TenantId = s.TenantId;
            DefaultRecordCount = s.DefaultRecordCount;
            DefaultBatchSize = s.DefaultBatchSize;
            DefaultDop = s.DefaultDop;
            DarkTheme = s.DarkTheme;
            ReduceMotion = s.ReduceMotion;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var settings = new AppSettings(
                OrgUrl, ClientId, TenantId,
                DefaultRecordCount, DefaultBatchSize, DefaultDop,
                DarkTheme, ReduceMotion);

            await _settingsService.SaveAsync(settings);

            // Reset Dataverse connection when org URL changes.
            _connectionService.Reset();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        TestConnectionSucceeded = false;
        ConnectionTestResult = "Testing…";

        try
        {
            _connectionService.Reset();
            await _connectionService.GetOrganizationServiceAsync();
            ConnectionTestResult = "Connected ✓";
            TestConnectionSucceeded = true;
        }
        catch (Exception ex)
        {
            ConnectionTestResult = $"Failed: {ex.Message}";
            TestConnectionSucceeded = false;
            _logger.LogError(ex, "Connection test failed");
        }
    }
}

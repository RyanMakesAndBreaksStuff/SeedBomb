using DataGen.Bulk;
using DataGen.Bulk.Contracts;
using DataGen.Core.Metadata;
using DataGen.Desktop.Services.Auth;
using DataGen.Desktop.Services.Connections;
using DataGen.Desktop.Services.Dataverse;
using DataGen.Desktop.Services.Generation;
using DataGen.Desktop.Services.History;
using DataGen.Desktop.Services.Settings;
using DataGen.Desktop.Services.Theme;
using DataGen.Desktop.ViewModels;
using DataGen.Desktop.ViewModels.Controls;
using DataGen.Desktop.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace DataGen.Desktop;

/// <summary>WPF application entry point. Hosts the generic host and owns window lifetime.</summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>Gets the DI container after <see cref="OnStartup"/> has run.</summary>
    public IServiceProvider Services => _host!.Services;

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var builder = Host.CreateApplicationBuilder();
            ConfigureServices(builder.Services);
            _host = builder.Build();
            await _host.StartAsync();

            // Apply saved theme before showing any window so both LoginWindow
            // and MainWindow render correctly from the first frame.
            var settings = _host.Services.GetRequiredService<ISettingsService>();
            var savedSettings = await settings.LoadAsync();
            DesignThemeManager.Apply(savedSettings.DarkTheme, savedSettings.PaletteId);

            // Attempt silent token acquisition before showing any window.
            // Pass nint.Zero to suppress any interactive popup — silent-only path.
            var auth = _host.Services.GetRequiredService<IAuthService>();
            var result = await auth.SignInAsync(nint.Zero);

            if (result.Succeeded)
            {
                ShowMainWindow(result.DisplayName ?? string.Empty);
            }
            else
            {
                // W1-A will wire LoginWindow.LoginSucceeded → ShowMainWindow.
                var loginWindow = _host.Services.GetRequiredService<LoginWindow>();
                loginWindow.Show();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup failed: {ex.Message}", "DataGen",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Shows the main window and populates header user info.</summary>
    /// <param name="displayName">The signed-in user's display name.</param>
    internal void ShowMainWindow(string displayName)
    {
        var vm = _host!.Services.GetRequiredService<MainWindowViewModel>();
        vm.UserDisplayName = displayName;

        var settings = _host.Services.GetRequiredService<ISettingsService>();
        var s = settings.LoadAsync().GetAwaiter().GetResult();
        var profiles = _host.Services.GetRequiredService<IConnectionProfileService>();
        var profile = profiles.GetLastUsedAsync().GetAwaiter().GetResult();
        vm.OrgUrl = profile?.EnvironmentUrl ?? s.OrgUrl;

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection sc)
    {
        // WPF UI framework services
        sc.AddSingleton<ISnackbarService, SnackbarService>();
        sc.AddSingleton<IContentDialogService, ContentDialogService>();

        // App services — all singleton (one app lifetime)
        sc.AddSingleton<ISettingsService, JsonSettingsService>();
        sc.AddSingleton<IConnectionProfileService, JsonConnectionProfileService>();
        sc.AddSingleton<IAuthService, ProfileAuthService>();
        sc.AddSingleton<IDataverseConnectionService, DataverseConnectionService>();
        sc.AddSingleton<IMetadataProvider, DataverseMetadataService>();
        sc.AddSingleton<IGenerationPipeline, GenerationPipeline>();
        sc.AddSingleton<IRunHistoryService, JsonRunHistoryService>();
        sc.AddSingleton<IWpfGenerationService, WpfGenerationService>();

        // Windows — singleton so only one instance exists at a time
        sc.AddSingleton<MainWindow>();
        sc.AddSingleton<LoginWindow>();

        // Window ViewModels — singleton to match singleton window lifetime
        sc.AddSingleton<MainWindowViewModel>();

        // Page/control ViewModels — transient so each page/control gets a fresh instance
        sc.AddTransient<LoginWindowViewModel>();
        sc.AddTransient<ConnectionManagerViewModel>();
        sc.AddTransient<EntitySelectorViewModel>();
        sc.AddTransient<GenerateViewModel>();
        sc.AddTransient<HistoryViewModel>();
        sc.AddTransient<SettingsViewModel>();

        // Real pages — NavigationView resolves these from DI via SetServiceProvider
        sc.AddTransient<DataGen.Desktop.Views.Pages.GeneratePage>();
        sc.AddTransient<DataGen.Desktop.Views.Pages.HistoryPage>();
        sc.AddTransient<DataGen.Desktop.Views.Pages.SettingsPage>();
    }
}

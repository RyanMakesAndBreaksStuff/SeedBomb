using DataGen.Bulk;
using DataGen.Bulk.Contracts;
using DataGen.Core.Metadata;
using Seedbomb.ViewModels;
using Seedbomb.ViewModels.Controls;
using Seedbomb.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seedbomb.Services;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Dataverse;
using Seedbomb.Services.Generation;
using Seedbomb.Services.History;
using Seedbomb.Services.Profiles;
using Seedbomb.Services.Settings;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Theme;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace Seedbomb;

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
#if DEBUG
            // Host.CreateApplicationBuilder defaults to Production when DOTNET_ENVIRONMENT is unset.
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
#endif
            var builder = Host.CreateApplicationBuilder();
            ConfigureServices(builder.Services);
            _host = builder.Build();
            await _host.StartAsync();

            // Resolving (not just registering) creates the singleton now; its Dispose() runs
            // automatically when the host's ServiceProvider is disposed in OnExit.
            _host.Services.GetRequiredService<TrayIconService>();

            // Apply saved theme before showing any window so both LoginWindow
            // and MainWindow render correctly from the first frame.
            var settings = _host.Services.GetRequiredService<ISettingsService>();
            var savedSettings = await settings.LoadAsync();
            DesignThemeManager.ReduceMotion = savedSettings.ReduceMotion;
            DesignThemeManager.Apply(savedSettings.DarkTheme, savedSettings.PaletteId);

            // Attempt silent token acquisition before showing any window.
            // Pass nint.Zero to suppress any interactive popup — silent-only path.
            // MSAL may emit first-chance RPC/COM exceptions (0x6BA/0x71A) against WAM; those
            // are handled inside SignInAsync and must not crash startup.
            var auth = _host.Services.GetRequiredService<IAuthService>();
            AuthResult result;
            try
            {
                result = await auth.SignInAsync(nint.Zero);
            }
            catch (Exception ex) when (ex is Microsoft.Identity.Client.MsalException
                or System.Runtime.InteropServices.COMException
                or InvalidOperationException)
            {
                result = new AuthResult(false, null, ex.Message);
            }

            if (result.Succeeded)
                await ShowMainWindow(result.DisplayName ?? string.Empty);
            else
            {
                // W1-A will wire LoginWindow.LoginSucceeded → ShowMainWindow.
                var loginWindow = _host.Services.GetRequiredService<LoginWindow>();
                loginWindow.Show();
            }
        }
        catch (Exception ex)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DataGen", "startup-error.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, ex.ToString());
            }
            catch { /* best-effort diagnostic */ }

            MessageBox.Show($"Startup failed: {ex}", "DataGen",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Shows the main window and populates header user info.</summary>
    /// <param name="displayName">The signed-in user's display name.</param>
    internal async Task ShowMainWindow(string displayName)
    {
        var vm = _host!.Services.GetRequiredService<MainWindowViewModel>();
        vm.UserDisplayName = displayName;

        var settings = _host.Services.GetRequiredService<ISettingsService>();
        var s = await settings.LoadAsync();
        var profiles = _host.Services.GetRequiredService<IConnectionProfileService>();
        var profile = await profiles.GetLastUsedAsync();
        vm.OrgUrl = profile?.EnvironmentUrl ?? s.OrgUrl;

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        Current.MainWindow = mainWindow;
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch
            {
                // Hosted services may already be tearing down; continue to Dispose.
            }

            try
            {
                _host.Dispose();
            }
            catch
            {
                // ServiceClient / tray / MSAL dispose often throw first-chance RPC/COM on exit.
            }

            _host = null;
        }
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection sc)
    {
        // WPF UI framework services
        sc.AddSingleton<ISnackbarService, SnackbarService>();
        sc.AddSingleton<IContentDialogService, ContentDialogService>();
        sc.AddSingleton<TrayIconService>();

        // App services — all singleton (one app lifetime)
        sc.AddSingleton<ISettingsService, JsonSettingsService>();
        sc.AddSingleton<IConnectionProfileService, JsonConnectionProfileService>();
        sc.AddSingleton<IAuthService, ProfileAuthService>();
        sc.AddSingleton<IDataverseConnectionService, DataverseConnectionService>();
        sc.AddSingleton<IMetadataProvider, DataverseMetadataService>();
        sc.AddSingleton<IGenerationPipeline, GenerationPipeline>();
        sc.AddSingleton<IRunHistoryService, JsonRunHistoryService>();
        sc.AddSingleton<IWpfGenerationService, WpfGenerationService>();
        sc.AddSingleton<IProfileService, JsonProfileService>();

        // Windows — singleton so only one instance exists at a time
        sc.AddSingleton<MainWindow>();
        sc.AddSingleton<LoginWindow>(sp =>
        {
            var loginVm = ActivatorUtilities.CreateInstance<LoginWindowViewModel>(sp);
            var loginConnections = ActivatorUtilities.CreateInstance<ConnectionManagerViewModel>(sp);
            return new LoginWindow(loginVm, loginConnections);
        });

        // Window ViewModels — singleton to match singleton window lifetime
        sc.AddSingleton<MainWindowViewModel>();

        // Page/control ViewModels — transient so each page/control gets a fresh instance
        sc.AddTransient<LoginWindowViewModel>();
        sc.AddSingleton<ConnectionManagerViewModel>();
        sc.AddTransient<EntitySelectorViewModel>();
        sc.AddSingleton<GenerateViewModel>();
        sc.AddTransient<ProfilesViewModel>();
        sc.AddTransient<HistoryViewModel>();
        sc.AddTransient<SettingsViewModel>();

        sc.AddSingleton<IAppNavigator, NavigationViewNavigator>();
        sc.AddSingleton<RulesNavigationRequest>();
        sc.AddSingleton<RunViewModel>();
        sc.AddTransient<RuleEditorViewModel>();

        // Real pages — NavigationView resolves these from DI via SetServiceProvider
        sc.AddSingleton<Seedbomb.Views.Pages.GeneratePage>();
        sc.AddTransient<Seedbomb.Views.Pages.HistoryPage>();
        sc.AddTransient<Seedbomb.Views.Pages.SettingsPage>();
        sc.AddTransient<Seedbomb.Views.Pages.ProfilesPage>();
        sc.AddTransient<Seedbomb.Views.Pages.RulesPage>();
        sc.AddTransient<Seedbomb.Views.Pages.RunSummaryPage>();
        sc.AddTransient<Seedbomb.Views.Pages.ConnectionsPage>();
    }
}

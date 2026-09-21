using SeedBomb.Bulk;
using SeedBomb.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seedbomb.Resources;
using Seedbomb.Services;
using Seedbomb.Services.About;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Dataverse;
using Seedbomb.Services.Diagnostics;
using Seedbomb.Services.Generation;
using Seedbomb.Services.History;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Profiles;
using Seedbomb.Services.Settings;
using Seedbomb.Services.Theme;
using Seedbomb.ViewModels;
using Seedbomb.ViewModels.Controls;
using Seedbomb.Views.Windows;
using System.Windows;
using Wpf.Ui;

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
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SplashWindow? splash = null;
        try
        {
            splash = new SplashWindow();
            splash.Show();
            splash.SetStatus("Starting services...");

#if DEBUG
            // Host.CreateApplicationBuilder defaults to Production when DOTNET_ENVIRONMENT is unset.
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
#endif
            var builder = Host.CreateApplicationBuilder();
            ConfigureServices(builder.Services);
            builder.Logging.AddProvider(new FileLoggerProvider());
            _host = builder.Build();
            await _host.StartAsync();

            // Apply saved theme before showing MainWindow so it renders correctly
            // from the first frame.
            splash.SetStatus("Applying your theme...");
            var settings = _host.Services.GetRequiredService<ISettingsService>();
            var savedSettings = await settings.LoadAsync();
            DesignThemeManager.Apply(savedSettings.DarkTheme, savedSettings.PaletteId);

            // Attempt silent token acquisition before showing any window.
            // Pass nint.Zero to suppress any interactive popup — silent-only path.
            // MSAL may emit first-chance RPC/COM exceptions (0x6BA/0x71A) against WAM; those
            // are handled inside SignInAsync and must not crash startup.
            splash.SetStatus("Restoring your session...");
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

            // Always show MainWindow — its "Sign in to continue" overlay covers a failed
            // silent attempt, and the first-run overlay already covers zero profiles.
            splash.SetStatus("Preparing your workspace...");
            await ShowMainWindow(result.DisplayName ?? string.Empty, result.Succeeded);
            // Resolving (not just registering) creates the singleton now; its Dispose() runs
            // automatically when the host's ServiceProvider is disposed in OnExit.
            _host.Services.GetRequiredService<TrayIconService>();
            splash.SetStatus("Ready");
            splash.SetProgress(1.0);
        }
        catch (Exception ex)
        {
            splash?.Close();
            CrashLog.Write(ex);
            MessageBox.Show($"Startup failed: {ex}", "SeedBomb",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // Learn: DispatcherUnhandledException has no protected virtual counterpart, so an
    // Application subclass must subscribe explicitly. Handled=true is set only for
    // exceptions the process can actually continue past; the rest keep WPF's shutdown.
    // https://learn.microsoft.com/dotnet/api/system.windows.application.dispatcherunhandledexception
    private void OnDispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
        if (e.Exception is OutOfMemoryException or StackOverflowException or AccessViolationException)
            return;

        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to the crash log.",
            "SeedBomb", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Shows the main window and populates header user info.</summary>
    /// <param name="displayName">The signed-in user's display name.</param>
    /// <param name="signedIn">Whether sign-in already succeeded (false shows the "Sign in to
    /// continue" overlay once a connection profile exists).</param>
    internal async Task ShowMainWindow(string displayName, bool signedIn = true)
    {
        var vm = _host!.Services.GetRequiredService<MainWindowViewModel>();
        vm.UserDisplayName = displayName;

        var profiles = _host.Services.GetRequiredService<IConnectionProfileService>();
        var profile = await profiles.GetLastUsedAsync();
        vm.OrgUrl = profile?.EnvironmentUrl ?? string.Empty;

        // A failed silent attempt only means "needs sign-in" when there was a profile to
        // reconnect to. With zero profiles, SignInAsync fails with "no profile configured" —
        // that's FirstRunOverlay's case, not a stale-session one, and NeedsSignIn must not
        // latch true here or it reappears the moment the first profile is saved (before its
        // own connect attempt has even run).
        vm.NeedsSignIn = profile is not null && !signedIn;

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        Current.MainWindow = mainWindow;
        mainWindow.Show();
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
        sc.AddSingleton<SeedBomb.Bulk.ThrottlePolicy>();
        sc.AddTransient<ILookupRecordSource, LookupRecordSource>();
        sc.AddTransient<LookupRecordPickerViewModel>();
        sc.AddTransient<Func<LookupRecordPickerViewModel>>(sp =>
            () => sp.GetRequiredService<LookupRecordPickerViewModel>());
        sc.AddTransient<ILookupRecordPicker, LookupRecordPickerService>();
        sc.AddSingleton<GenerationPipeline>();
        sc.AddSingleton<IRunHistoryService, JsonRunHistoryService>();
        sc.AddSingleton<IWpfGenerationService, WpfGenerationService>();
        sc.AddSingleton<IProfileService, JsonProfileService>();
        sc.AddSingleton<IEmbeddedResourceReader>(_ =>
            new AssemblyResourceReader(typeof(App).Assembly));
        sc.AddSingleton<IThirdPartyNoticeService, ThirdPartyNoticeService>();
        sc.AddSingleton<IAboutDialogService, AboutDialogService>();
        sc.AddSingleton<IUriLauncher, ProcessUriLauncher>();

        // Windows — singleton so only one instance exists at a time
        sc.AddSingleton<MainWindow>();

        // Window ViewModels — singleton to match singleton window lifetime
        sc.AddSingleton<MainWindowViewModel>();

        // Page/control ViewModels — transient so each page/control gets a fresh instance
        sc.AddSingleton<ConnectionManagerViewModel>();
        sc.AddTransient<EntitySelectorViewModel>();
        sc.AddSingleton<GenerateViewModel>();
        sc.AddTransient<ProfilesViewModel>();
        sc.AddTransient<HistoryViewModel>();
        sc.AddTransient<SettingsViewModel>();
        sc.AddTransient<AboutViewModel>();

        sc.AddSingleton<IAppNavigator, NavigationViewNavigator>();
        sc.AddSingleton<RulesNavigationRequest>();
        sc.AddSingleton<RunViewModel>();
        sc.AddTransient<RuleMetadataLoader>();
        sc.AddTransient(sp => new RuleEditorServices(
            sp.GetService<IContentDialogService>(),
            sp.GetService<ISnackbarService>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RuleEditorViewModel>>(),
            sp.GetService<ILookupRecordPicker>()));
        sc.AddTransient<RuleEditorViewModel>();

        // Real pages — NavigationView resolves these from DI via SetServiceProvider
        sc.AddSingleton<Seedbomb.Views.Pages.GeneratePage>();
        sc.AddTransient<Seedbomb.Views.Pages.HistoryPage>();
        sc.AddTransient<Seedbomb.Views.Pages.SettingsPage>();
        sc.AddTransient<Seedbomb.Views.Pages.AboutPage>();
        sc.AddTransient<Seedbomb.Views.Pages.ProfilesPage>();
        sc.AddTransient<Seedbomb.Views.Pages.RulesPage>();
        sc.AddTransient<Seedbomb.Views.Pages.RunSummaryPage>();
        sc.AddTransient<Seedbomb.Views.Pages.ConnectionsPage>();
    }
}

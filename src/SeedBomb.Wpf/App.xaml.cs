using SeedBomb.Bulk;
using SeedBomb.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeedBomb.Resources;
using SeedBomb.Services;
using SeedBomb.Services.About;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.Services.Diagnostics;
using SeedBomb.Services.Generation;
using SeedBomb.Services.History;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.Services.Settings;
using SeedBomb.Services.Theme;
using SeedBomb.ViewModels;
using SeedBomb.ViewModels.Controls;
using SeedBomb.Views.Windows;
using System.Windows;
using Wpf.Ui;

namespace SeedBomb;

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
        AppDomain.CurrentDomain.UnhandledException += LogUnhandledException;
        TaskScheduler.UnobservedTaskException += LogUnobservedTaskException;
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
            // SignInAsync never throws (CR-006): MSAL/WAM, store and secret failures all come
            // back as a failed AuthResult and land on the sign-in overlay.
            splash.SetStatus("Restoring your session...");
            var auth = _host.Services.GetRequiredService<IAuthService>();
            var sessionGate = _host.Services.GetRequiredService<RunSessionGate>();
            AuthResult result;
            var startupLease = await sessionGate.AcquireAsync();
            try
            {
                result = await auth.SignInAsync(nint.Zero);
            }
            finally
            {
                startupLease.Dispose();
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
            // WR-002: never close the splash here. It is the only window, so closing it starts
            // WPF's own Shutdown() with exit code 0, and Shutdown(1) cannot override a shutdown
            // already under way. Shutdown(1) closes the splash itself.
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

    // IN-001: DispatcherUnhandledException only covers the UI dispatcher thread. A failure in a
    // fire-and-forget async void or an unawaited Task (e.g. ProfileAuthService.cs:517,
    // GenerateViewModel.cs:134, ConnectionManagerViewModel.cs:419) reached neither handler and
    // went unrecorded. Static and internal so tests can call them directly without constructing
    // a second System.Windows.Application in-process (only one is allowed per process).
    // https://learn.microsoft.com/dotnet/api/system.appdomain.unhandledexception
    internal static void LogUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            CrashLog.Write(ex);
    }

    // https://learn.microsoft.com/dotnet/api/system.threading.tasks.taskscheduler.unobservedtaskexception
    internal static void LogUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
        // Logged, not swallowed: SetObserved only stops the finalizer thread from re-throwing.
        // .NET does not crash the process on an unobserved task exception by default; this keeps
        // that existing behavior unchanged.
        e.SetObserved();
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
        // WR-012: the snackbar presenter is set in MainWindow.OnWindowLoaded, which MainWindow's
        // constructor subscribed first, so this later Loaded handler always runs after it.
        var snackbar = _host.Services.GetRequiredService<ISnackbarService>();
        foreach (var (title, warning) in new[]
                 {
                     ("Data folder", AppPaths.MigrationWarning),
                     ("Settings", _host.Services.GetRequiredService<ISettingsService>().LoadWarning),
                 })
        {
            if (warning is not null)
                mainWindow.Loaded += (_, _) => snackbar.Show(
                    title, warning, Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(6));
        }
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
        sc.AddSingleton<RunSessionGate>();
        sc.AddSingleton<IAuthService, ProfileAuthService>();
        sc.AddSingleton<IDataverseConnectionService, DataverseConnectionService>();
        sc.AddSingleton<IMetadataProvider, DataverseMetadataService>();
        sc.AddSingleton<SeedBomb.Bulk.ThrottlePolicy>();
        sc.AddTransient<ILookupRecordSource, LookupRecordSource>();
        // IN-005: ActivatorUtilities instances are not tracked by the (root) provider, so the
        // picker's `using` is their only owner instead of the container pinning each until exit.
        sc.AddTransient<Func<LookupRecordPickerViewModel>>(sp =>
            () => ActivatorUtilities.CreateInstance<LookupRecordPickerViewModel>(sp));
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
        sc.AddSingleton<SeedBomb.Views.Pages.GeneratePage>();
        sc.AddTransient<SeedBomb.Views.Pages.HistoryPage>();
        sc.AddTransient<SeedBomb.Views.Pages.SettingsPage>();
        sc.AddTransient<SeedBomb.Views.Pages.AboutPage>();
        sc.AddTransient<SeedBomb.Views.Pages.ProfilesPage>();
        sc.AddTransient<SeedBomb.Views.Pages.RulesPage>();
        sc.AddTransient<SeedBomb.Views.Pages.RunSummaryPage>();
        sc.AddTransient<SeedBomb.Views.Pages.ConnectionsPage>();
    }
}

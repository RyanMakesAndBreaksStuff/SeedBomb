using Microsoft.Extensions.Logging;
using Moq;
using SeedBomb.Core.Contracts;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.Services.Generation;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void SelectPaletteCommandSetsPaletteId()
    {
        var vm = new SettingsViewModel(
            Mock.Of<ISettingsService>(),
            Mock.Of<ILogger<SettingsViewModel>>());

        Assert.True(vm.SelectPaletteCommand.CanExecute("notes"));
        vm.SelectPaletteCommand.Execute("notes");
        Assert.Equal("notes", vm.PaletteId);
    }

    [Fact]
    public async Task LoadAsyncMigratesLegacyPaletteId()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppSettings.Default with { PaletteId = "violet-ink" });

        var vm = new SettingsViewModel(settings.Object, Mock.Of<ILogger<SettingsViewModel>>());
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("kiln", vm.PaletteId);
    }

    [Fact]
    public async Task SaveAsync_ShowsDangerSnackbar_WhenPersistFails()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppSettings.Default);
        settings.Setup(s => s.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var snackbar = new Mock<ISnackbarService>();
        var vm = new SettingsViewModel(
            settings.Object, new Mock<ILogger<SettingsViewModel>>().Object, snackbar.Object);

        await vm.SaveCommand.ExecuteAsync(null);

        snackbar.Verify(s => s.Show(
            It.IsAny<string>(), It.IsAny<string>(),
            ControlAppearance.Danger, It.IsAny<IconElement?>(), It.IsAny<TimeSpan>()),
            Times.Once);
    }

    [Fact]
    public async Task SaveAsync_ClampsDefaultDopToMaxDop()
    {
        AppSettings? saved = null;
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppSettings.Default);
        settings.Setup(s => s.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var vm = new SettingsViewModel(settings.Object, Mock.Of<ILogger<SettingsViewModel>>());
        await vm.LoadCommand.ExecuteAsync(null);
        vm.DefaultDop = 64;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(GenerationLimits.MaxDop, saved?.DefaultDop);
    }

    [Fact]
    public async Task KeepRunSheetOpen_RoundTripsThroughLoadAndSave()
    {
        AppSettings? saved = null;
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppSettings.Default with { KeepRunSheetOpen = false });
        settings.Setup(s => s.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var vm = new SettingsViewModel(settings.Object, Mock.Of<ILogger<SettingsViewModel>>());
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.KeepRunSheetOpen);

        vm.KeepRunSheetOpen = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(saved?.KeepRunSheetOpen);
    }

    [Fact]
    public async Task SignOut_is_blocked_while_a_run_is_writing()
    {
        // WR-001: sign-out disposes the ServiceClient the running pipeline writes through.
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AppSettings.Default);
        var run = new RunViewModel { IsRunning = true };
        var vm = new SettingsViewModel(
            settings.Object, Mock.Of<ILogger<SettingsViewModel>>(), auth: Mock.Of<IAuthService>(), run: run);
        await vm.OnNavigatedToAsync();

        Assert.False(vm.SignOutCommand.CanExecute(null));

        var raised = false;
        vm.SignOutCommand.CanExecuteChanged += (_, _) => raised = true;
        run.IsRunning = false;

        Assert.True(raised);
        Assert.True(vm.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadAsync_WhenLoadFails_BlocksSaveUntilNextSuccessfulLoad()
    {
        // WR-004: a load failure left _loadedSettings at AppSettings.Default. Any later appearance
        // toggle or a Save click then persisted AppSettings.Default over the user's real record
        // count, batch size, DOP and KeepRunSheetOpen.
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("locked by antivirus"));

        var snackbar = new Mock<ISnackbarService>();
        var vm = new SettingsViewModel(
            settings.Object, Mock.Of<ILogger<SettingsViewModel>>(), snackbar.Object);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.SaveCommand.CanExecute(null));
        snackbar.Verify(s => s.Show(
            It.IsAny<string>(), It.IsAny<string>(),
            ControlAppearance.Danger, It.IsAny<IconElement?>(), It.IsAny<TimeSpan>()),
            Times.Once);
    }

    [Fact]
    public async Task InFlightSignOut_BlocksRunStartupUntilResetCompletes()
    {
        var gate = new RunSessionGate();
        var auth = new MutableAuth
        {
            ActiveProfile = new ConnectionProfile
            {
                Name = "Dev",
                EnvironmentUrl = "https://dev.crm.dynamics.com",
            },
            CurrentUserDisplayName = "ada",
        };
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = new Mock<IDataverseConnectionService>();
        connections.Setup(c => c.ResetAsync()).Returns(async () =>
        {
            resetEntered.TrySetResult();
            await releaseReset.Task;
        });
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AppSettings.Default);
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ConnectionProfile?)null);
        var calls = 0;
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new GenerationResult { Elapsed = TimeSpan.FromSeconds(1) });
            });
        var run = new RunViewModel(
            generation: gen.Object, settings: settings.Object, auth: auth, sessionGate: gate);
        var vm = new SettingsViewModel(
            settings.Object,
            Mock.Of<ILogger<SettingsViewModel>>(),
            auth: auth,
            connections: connections.Object,
            profiles: profiles.Object,
            run: run,
            sessionGate: gate);

        var signingOut = vm.SignOutCommand.ExecuteAsync(null);
        await resetEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var running = run.ExecuteAsync(
            new GenerationConfig
            {
                EntityLogicalNames = ["account"],
                RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
            },
            TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(0, calls);
            Assert.False(run.IsRunning);
        }
        finally
        {
            releaseReset.TrySetResult();
        }

        await signingOut.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => running);
        Assert.Equal(0, calls);
        Assert.Null(auth.ActiveProfile);
    }

    private sealed class MutableAuth : IAuthService
    {
        public ConnectionProfile? ActiveProfile { get; set; }
        public string? CurrentUserDisplayName { get; set; }
        public event EventHandler? SignedOut;
        public event EventHandler? ActiveProfileChanged;

        public Task<AuthResult> SignInAsync(nint parentHwnd, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult(false, null, null));

        public Task<AuthResult> SignInAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult(true, CurrentUserDisplayName, null));

        public Task<AuthResult> TryConnectAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult(false, null, null));

        public Task SignOutAsync(CancellationToken ct = default)
        {
            ActiveProfile = null;
            CurrentUserDisplayName = null;
            SignedOut?.Invoke(this, EventArgs.Empty);
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task ForgetProfileAsync(ConnectionProfile profile, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default) => Task.FromResult("token");
    }
}

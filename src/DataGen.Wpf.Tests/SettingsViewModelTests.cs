using Moq;
using Seedbomb.Services.Settings;
using Seedbomb.Services.Theme;
using Seedbomb.ViewModels;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void SelectPaletteCommandSetsPaletteId()
    {
        var vm = new SettingsViewModel(
            Mock.Of<ISettingsService>(),
            Mock.Of<ILogger<SettingsViewModel>>());

        Assert.True(vm.SelectPaletteCommand.CanExecute("graphite"));
        vm.SelectPaletteCommand.Execute("graphite");
        Assert.Equal("graphite", vm.PaletteId);
    }

    [Fact]
    public async Task LoadAsyncMigratesLegacyPaletteId()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppSettings.Default with { PaletteId = "slate-steel" });

        var vm = new SettingsViewModel(settings.Object, Mock.Of<ILogger<SettingsViewModel>>());
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("graphite", vm.PaletteId);
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
}

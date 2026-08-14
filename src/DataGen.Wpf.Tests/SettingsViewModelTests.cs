using Moq;
using Seedbomb.Services.Settings;
using Seedbomb.Services.Theme;
using Seedbomb.ViewModels;
using Microsoft.Extensions.Logging;
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
}

using Moq;
using SeedBomb.Services.About;
using SeedBomb.ViewModels;
using Wpf.Ui;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class AboutViewModelTests
{
    [Fact]
    public void Metadata_uses_assembly_and_runtime_apis()
    {
        var vm = Create();
        Assert.Equal("SeedBomb", vm.ProductName);
        Assert.Equal("Synthetic data generation for Microsoft Dataverse", vm.Description);
        Assert.Equal("Ryan Rettinger", vm.Author);
        Assert.False(string.IsNullOrWhiteSpace(vm.Version));
        Assert.DoesNotContain("0.0.0.0", vm.Version, StringComparison.Ordinal);
        Assert.StartsWith(".NET", vm.Runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_commands_open_canonical_https_urls()
    {
        var launcher = new Mock<IUriLauncher>();
        launcher.Setup(l => l.CanOpen(It.IsAny<string?>()))
            .Returns((string? url) => new ProcessUriLauncher().CanOpen(url));
        launcher.Setup(l => l.TryOpen(It.IsAny<string?>(), out It.Ref<string>.IsAny)).Returns(true);
        var vm = Create(launcher.Object);

        Assert.True(vm.OpenUrlCommand.CanExecute(AppInfo.SourceCodeUrl));
        vm.OpenUrlCommand.Execute(AppInfo.SourceCodeUrl);
        vm.OpenUrlCommand.Execute(AppInfo.DocumentationUrl);
        vm.OpenUrlCommand.Execute(AppInfo.IssuesUrl);

        launcher.Verify(l => l.TryOpen(AppInfo.SourceCodeUrl, out It.Ref<string>.IsAny), Times.Once);
        launcher.Verify(l => l.TryOpen(AppInfo.DocumentationUrl, out It.Ref<string>.IsAny), Times.Once);
        launcher.Verify(l => l.TryOpen(AppInfo.IssuesUrl, out It.Ref<string>.IsAny), Times.Once);
        Assert.False(vm.OpenUrlCommand.CanExecute("http://example.com"));
    }

    internal static AboutViewModel Create(IUriLauncher? launcher = null)
    {
        var notices = new Mock<IThirdPartyNoticeService>();
        notices.Setup(s => s.Load()).Returns(new ThirdPartyNoticeLoadResult([], []));
        notices.Setup(s => s.GetFeatured(It.IsAny<ThirdPartyNoticeLoadResult>())).Returns([]);
        return new AboutViewModel(
            notices.Object,
            launcher ?? Mock.Of<IUriLauncher>(l => l.CanOpen(It.IsAny<string?>()) == true),
            Mock.Of<IAboutDialogService>(),
            Mock.Of<ISnackbarService>());
    }
}

using Microsoft.Extensions.DependencyInjection;
using Moq;
using SeedBomb;
using SeedBomb.Services.About;
using SeedBomb.ViewModels;
using SeedBomb.Wpf.Tests.Views;
using System.Reflection;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;
using TextBox = System.Windows.Controls.TextBox;

namespace SeedBomb.Wpf.Tests;

[Collection("StaUi")]
public sealed class AboutLicenseDialogTests
{
    [StaFact]
    public void Application_command_displays_embedded_license()
    {
        var shown = new List<ContentDialog>();
        using var provider = CreateProvider(shown);
        var vm = provider.GetRequiredService<AboutViewModel>();
        vm.ShowAppLicenseCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        var dialog = Assert.Single(shown);
        Assert.Equal($"{AppInfo.ProductName} {vm.Version} — License", dialog.Title);
        var expected = new ThirdPartyNoticeService(
            new AssemblyResourceReader(typeof(App).Assembly)).ReadAppLicense();
        Assert.StartsWith("BSD 3-Clause License", expected, StringComparison.Ordinal);
        AssertText(dialog, expected);
    }

    [StaFact]
    public void Every_featured_command_displays_its_own_license()
    {
        var shown = new List<ContentDialog>();
        using var provider = CreateProvider(shown);
        var vm = provider.GetRequiredService<AboutViewModel>();
        var notices = provider.GetRequiredService<IThirdPartyNoticeService>();
        Assert.NotEmpty(vm.Featured);
        foreach (var component in vm.Featured)
        {
            shown.Clear();
            vm.ShowComponentLicenseCommand.ExecuteAsync(component).GetAwaiter().GetResult();
            var dialog = Assert.Single(shown);
            Assert.Equal($"{component.Name} {component.Version} — {component.License}", dialog.Title);
            var expected = notices.ReadLicense(component);
            Assert.NotEqual("License text is not available for this component.", expected);
            AssertText(dialog, expected);
        }
    }

    [StaFact]
    public void Missing_resources_display_existing_reader_messages()
    {
        var shown = new List<ContentDialog>();
        using var provider = CreateProvider(shown, Mock.Of<IEmbeddedResourceReader>());
        var vm = provider.GetRequiredService<AboutViewModel>();
        vm.ShowAppLicenseCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        AssertText(Assert.Single(shown), "The application license could not be loaded.");

        shown.Clear();
        var component = new ThirdPartyComponent("Missing", "1.0", "", "MIT", "",
            "https://example.com", "SeedBomb.Licenses.Missing.txt", true);
        vm.ShowComponentLicenseCommand.ExecuteAsync(component).GetAwaiter().GetResult();
        AssertText(Assert.Single(shown), "License text is not available for this component.");
    }

    private static ServiceProvider CreateProvider(List<ContentDialog> shown,
        IEmbeddedResourceReader? resources = null)
    {
        var services = new ServiceCollection();
        var configure = typeof(App).GetMethod("ConfigureServices",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(configure);
        configure.Invoke(null, [services]);
        var transport = new Mock<IContentDialogService>();
        transport.Setup(s => s.ShowAsync(It.IsAny<ContentDialog>(), It.IsAny<CancellationToken>()))
            .Callback<ContentDialog, CancellationToken>((dialog, _) => shown.Add(dialog))
            .ReturnsAsync(ContentDialogResult.None);
        services.AddSingleton<IContentDialogService>(transport.Object);
        if (resources is not null)
            services.AddSingleton<IEmbeddedResourceReader>(resources);
        return services.BuildServiceProvider();
    }

    private static void AssertText(ContentDialog dialog, string expected)
    {
        Assert.Equal("Close", dialog.CloseButtonText);
        var text = Assert.IsType<TextBox>(dialog.Content);
        Assert.Equal(expected, text.Text);
        Assert.True(text.IsReadOnly);
        Assert.Equal(ScrollBarVisibility.Auto, text.VerticalScrollBarVisibility);
    }
}

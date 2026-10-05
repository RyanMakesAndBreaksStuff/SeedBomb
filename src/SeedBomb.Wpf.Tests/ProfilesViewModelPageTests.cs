using Moq;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.ViewModels;
using SeedBomb.Views.Pages;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class ProfilesViewModelPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-profiles-page", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task OnNavigatedToAsync_SetsError_WhenTheProfileStoreThrows()
    {
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("profile store is corrupt"));

        var vm = ProfilesHost.Create(profiles.Object);

        // Must not throw — WPF-UI notifies INavigationAware from an async void method,
        // so anything that escapes here crashes the process.
        var ex = await Record.ExceptionAsync(() => vm.OnNavigatedToAsync());

        Assert.Null(ex);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task RefreshProjectsVersionAndSummary()
    {
        Directory.CreateDirectory(_root);
        var svc = new JsonProfileService(_root);
        var ct = TestContext.Current.CancellationToken;
        await svc.SaveAsync(new Profile(1, "Acme", "demo", 40719,
        [
            new ProfileTable("account", 500, null),
            new ProfileTable("contact", 9000, null),
        ]), ct);

        var vm = ProfilesHost.Create(svc);
        await vm.RefreshCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Items);
        Assert.Equal("Acme", row.Name);
        Assert.Equal("v2", row.VersionLabel);
        Assert.Contains("2 tables", row.SummaryLine, StringComparison.Ordinal);
        Assert.Equal("1 profile", vm.CountLabel);
    }

    [Fact]
    public async Task SearchTextFiltersItems()
    {
        Directory.CreateDirectory(_root);
        var svc = new JsonProfileService(_root);
        var ct = TestContext.Current.CancellationToken;
        await svc.SaveAsync(new Profile(1, "Alpha", null, null, [new ProfileTable("account", 1, null)]), ct);
        await svc.SaveAsync(new Profile(1, "Beta", null, null, [new ProfileTable("contact", 1, null)]), ct);

        var vm = ProfilesHost.Create(svc);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SearchText = "alp";

        Assert.Equal("Alpha", Assert.Single(vm.VisibleItems).Name);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task RefreshRestoresSelectedItemByName()
    {
        Directory.CreateDirectory(_root);
        var svc = new JsonProfileService(_root);
        var ct = TestContext.Current.CancellationToken;
        await svc.SaveAsync(new Profile(1, "Alpha", null, null, [new ProfileTable("account", 1, null)]), ct);
        await svc.SaveAsync(new Profile(1, "Beta", null, null, [new ProfileTable("contact", 1, null)]), ct);

        var vm = ProfilesHost.Create(svc);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single(i => i.Name == "Beta");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Beta", vm.SelectedItem?.Name);
    }

    [Fact]
    public async Task CycleSort_WhenStoreThrows_SetsErrorAndDoesNotPropagate()
    {
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("connections store is corrupt"));
        var vm = ProfilesHost.Create(profiles.Object);

        await vm.CycleSortCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("connections store is corrupt", vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditRules_WhenProfileUnreadable_SetsErrorAndDoesNotPropagate()
    {
        var (vm, profiles) = await ProfilesViewModelWithOneProfileAsync();
        profiles.Setup(p => p.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("profile exceeds 1 MiB"));

        await vm.EditRulesCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("profile exceeds 1 MiB", vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditRules_replaces_the_generate_handoff_and_routes_saves_to_the_board()
    {
        // Generate stamps these whenever it navigates away with tables selected.
        var request = new RulesNavigationRequest
        {
            TableName = "email",
            ReturnPage = typeof(GeneratePage),
            OnSaved = _ => { },
        };
        var board = ProfilesHost.Board();
        var (vm, _) = await ProfilesViewModelWithOneProfileAsync(request, board.Object);

        await vm.EditRulesCommand.ExecuteAsync(null);

        Assert.Equal("Acme", request.Profile?.Name);
        Assert.Null(request.TableName);
        Assert.Null(request.ReturnPage);
        request.OnSaved!(request.Profile!);
        board.Verify(b => b.ApplySavedProfileIfActive(request.Profile!), Times.Once);
    }

    private static async Task<(ProfilesViewModel Vm, Mock<IProfileService> Profiles)> ProfilesViewModelWithOneProfileAsync(
        RulesNavigationRequest? rulesRequest = null, IProfileBoard? board = null)
    {
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Acme" });
        profiles.Setup(p => p.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Profile(1, "Acme", null, null, [new ProfileTable("account", 1, null)]));

        var vm = ProfilesHost.Create(
            profiles.Object,
            rulesRequest: rulesRequest ?? new RulesNavigationRequest(),
            navigator: Mock.Of<IAppNavigator>(),
            board: board);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = Assert.Single(vm.Items);
        return (vm, profiles);
    }
}

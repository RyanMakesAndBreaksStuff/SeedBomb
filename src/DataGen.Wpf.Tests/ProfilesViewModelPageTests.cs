using Seedbomb.Services.Profiles;
using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class ProfilesViewModelPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-profiles-page", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
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

        var vm = new ProfilesViewModel(svc);
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

        var vm = new ProfilesViewModel(svc);
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

        var vm = new ProfilesViewModel(svc);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single(i => i.Name == "Beta");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Beta", vm.SelectedItem?.Name);
    }
}

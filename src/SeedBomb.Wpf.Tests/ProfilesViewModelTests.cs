using SeedBomb.Core.Rules;
using SeedBomb.Services.Profiles;
using SeedBomb.ViewModels;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class ProfilesViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dg-profiles-summary", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task BogusRule_RendersApiAndEndpointSummary()
    {
        var vm = await ProfilesViewModelWith(new BogusRule("NAME", "firstName", 1));

        Assert.Equal("bogus · NAME.firstName", vm.SelectedProfileRules.Single().OperationSummary);
    }

    [Fact]
    public async Task LookupRandomRule_RendersItsOpName()
    {
        // IN-004: it fell through to the CLR type name.
        var vm = await ProfilesViewModelWith(new LookupRandomRule());

        Assert.Equal("lookupRandom", vm.SelectedProfileRules.Single().OperationSummary);
    }

    private async Task<ProfilesViewModel> ProfilesViewModelWith(FieldRule rule)
    {
        Directory.CreateDirectory(_root);
        var svc = new JsonProfileService(_root);
        var ct = TestContext.Current.CancellationToken;
        await svc.SaveAsync(new Profile(
            Profile.CurrentProfileVersion,
            "bogus-summary",
            null,
            42,
            [new ProfileTable("account", 10, new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = rule,
            })]), ct);

        var vm = ProfilesHost.Create(svc);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = Assert.Single(vm.Items);
        return vm;
    }

    [Fact]
    public void ProfileListItem_HasNoUnmappedHintParameter()
    {
        // Project() never supplied it, so the summary's " · {hint}" branch was unreachable.
        Assert.DoesNotContain(
            typeof(ProfileListItem).GetConstructors().Single().GetParameters(),
            p => p.Name == "UnmappedRequiredHint");
    }
}

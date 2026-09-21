using SeedBomb.Core.Contracts;
using SeedBomb.Core.Metadata;
using Microsoft.Extensions.Logging;
using Moq;
using Seedbomb.ViewModels.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class EntitySelectorViewModelTests
{
    [Fact]
    public async Task CustomOnly_hides_non_custom_rows()
    {
        var vm = await LoadAsync(
            new EntitySummary("account", "Account", false),
            new EntitySummary("cr12_site", "Site Survey", true));

        Assert.Equal(2, vm.FilteredEntities.Count);
        vm.ShowCustomOnly = true;
        Assert.Equal(["Site Survey"], vm.FilteredEntities.Select(i => i.DisplayName).ToArray());
    }

    [Fact]
    public async Task ClearCheckedCommand_unchecks_all()
    {
        var vm = await LoadAsync(new EntitySummary("account", "Account", false));
        vm.EntityItems[0].IsSelected = true;
        Assert.True(vm.ClearCheckedCommand.CanExecute(null));

        vm.ClearCheckedCommand.Execute(null);

        Assert.Empty(vm.SelectedEntities);
        Assert.False(vm.EntityItems[0].IsSelected);
        Assert.False(vm.ClearCheckedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Search_and_custom_only_compose()
    {
        var vm = await LoadAsync(
            new EntitySummary("account", "Account", false),
            new EntitySummary("cr12_site", "Site Survey", true),
            new EntitySummary("cr12_insp", "Inspection", true));

        vm.ShowCustomOnly = true;
        vm.FilterText = "site";
        Assert.Equal(["Site Survey"], vm.FilteredEntities.Select(i => i.DisplayName).ToArray());
    }

    private static async Task<EntitySelectorViewModel> LoadAsync(params EntitySummary[] entities)
    {
        var metadata = new Mock<IMetadataProvider>();
        metadata
            .Setup(m => m.ListUserEntitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);
        var vm = new EntitySelectorViewModel(metadata.Object, Mock.Of<ILogger<EntitySelectorViewModel>>());
        await vm.LoadEntitiesCommand.ExecuteAsync(null);
        return vm;
    }
}

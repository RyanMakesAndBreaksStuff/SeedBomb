using SeedBomb.Core.Contracts;
using SeedBomb.Core.Metadata;
using Microsoft.Extensions.Logging;
using Moq;
using SeedBomb.ViewModels.Controls;
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

    [Fact]
    public async Task Stale_load_failure_does_not_overwrite_a_newer_successful_load()
    {
        // WR-011: a connection switch can dispose the ServiceClient the OLD load is still using.
        // That old load then fails with something other than OperationCanceledException and, with
        // no generation check, stomps the ErrorMessage/IsLoading the NEW load already set.
        var firstCall = new TaskCompletionSource<IReadOnlyList<EntitySummary>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new Mock<IMetadataProvider>();
        metadata.SetupSequence(m => m.ListUserEntitiesAsync(It.IsAny<CancellationToken>()))
            .Returns(firstCall.Task)
            .ReturnsAsync([new EntitySummary("contact", "Contact", false)]);

        var vm = new EntitySelectorViewModel(metadata.Object, Mock.Of<ILogger<EntitySelectorViewModel>>());

        var first = vm.LoadEntitiesCommand.ExecuteAsync(null);   // starts the load that will go stale
        await vm.LoadEntitiesCommand.ExecuteAsync(null);          // supersedes it and completes

        Assert.Equal(["Contact"], vm.EntityItems.Select(i => i.DisplayName).ToArray());

        firstCall.SetException(new ObjectDisposedException("ServiceClient"));
        await first;

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(["Contact"], vm.EntityItems.Select(i => i.DisplayName).ToArray());
        Assert.False(vm.IsLoading);
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

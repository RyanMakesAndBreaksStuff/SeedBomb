using Moq;
using Seedbomb.Services.Profiles;
using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class ProfilesPageHandoffTests
{
    private static Profile MakeProfile(string name) => new(
        Profile.CurrentProfileVersion,
        name,
        Description: null,
        Seed: 7,
        Tables: [new ProfileTable("account", 25, null)]);

    [Fact]
    public async Task LoadAsync_WithoutMetadataHost_ReportsErrorRatherThanSuccess()
    {
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Sales" });
        profiles.Setup(p => p.LoadAsync("Sales", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProfile("Sales"));

        var vm = new ProfilesViewModel(profiles.Object);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();

        // GetMetadata deliberately left null — simulates a host that forgot to wire itself.
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Null(vm.PendingImport);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task LoadAsync_WithMetadataHost_ProducesPendingImport()
    {
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Sales" });
        profiles.Setup(p => p.LoadAsync("Sales", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProfile("Sales"));

        var vm = new ProfilesViewModel(profiles.Object)
        {
            GetMetadata = () => new Dictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata>(
                StringComparer.OrdinalIgnoreCase),
            GetRunId = () => "run-1",
            ConfirmOverwrite = _ => true,
            IsBoardDirty = () => false,
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.NotNull(vm.PendingImport);
    }

    [Fact]
    public async Task OpenInBoard_WithPendingImport_RaisesProfileApplied()
    {
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Sales" });
        profiles.Setup(p => p.LoadAsync("Sales", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProfile("Sales"));

        var vm = new ProfilesViewModel(profiles.Object)
        {
            GetMetadata = () => new Dictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata>(
                StringComparer.OrdinalIgnoreCase),
            GetRunId = () => "run-1",
            ConfirmOverwrite = _ => true,
            IsBoardDirty = () => false,
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();
        await vm.LoadCommand.ExecuteAsync(null);

        ProfileImportReport? applied = null;
        vm.ProfileApplied += (_, report) => applied = report;

        Assert.True(vm.OpenInBoardCommand.CanExecute(null));
        vm.OpenInBoardCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.Same(vm.PendingImport, applied);
        Assert.True(vm.AppliedToBoard);
    }
}

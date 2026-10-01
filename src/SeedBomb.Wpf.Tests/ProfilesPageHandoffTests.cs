using SeedBomb.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using SeedBomb.Services.Generation;
using SeedBomb.Services.Profiles;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels;
using Wpf.Ui;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class ProfilesPageHandoffTests
{
    private static Profile MakeProfile(string name) => new(
        Profile.CurrentProfileVersion,
        name,
        Description: null,
        Seed: 7,
        Tables: [new ProfileTable("account", 25, null)]);

    // T1: same GenerateViewModel construction shape as GenerateViewModelStepTests.CreateViewModel,
    // trimmed to what these handoff tests need — a metadata-provider mock to drive EnsureMetadataAsync.
    private static GenerateViewModel MakeGenerateViewModel(out Mock<IMetadataProvider> metadataMock)
    {
        metadataMock = new Mock<IMetadataProvider>();
        return new GenerateViewModel(
            Mock.Of<ISettingsService>(),
            Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(),
            metadataMock.Object,
            Mock.Of<IProfileService>(),
            Mock.Of<IContentDialogService>(),
            new SeedBomb.ViewModels.RunViewModel(Mock.Of<IWpfGenerationService>()));
    }

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
    }

    // ── T1: cold-start profile metadata (no prior Rules visit) ────────────────

    [Fact]
    public async Task ColdStart_EnsureMetadataBeforePresentImport_AppliesTableWithNoNotImported()
    {
        var generate = MakeGenerateViewModel(out var metadataMock);
        metadataMock
            .Setup(m => m.GetEntityAsync("account", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityMetadata { LogicalName = "account" });

        // Simulates ProfilesPage's cold-start prefetch: metadata is ensured for the profile's
        // tables BEFORE ProfilesViewModel.PresentImport (via LoadCommand) ever runs — no
        // GoToRulesAsync visit involved.
        await generate.EnsureMetadataAsync(["account"], TestContext.Current.CancellationToken);

        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Sales" });
        profiles.Setup(p => p.LoadAsync("Sales", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProfile("Sales"));

        var vm = new ProfilesViewModel(profiles.Object)
        {
            GetMetadata = () => generate.EntityMetadataMap,
            GetRunId = () => generate.RunId,
            ConfirmOverwrite = _ => true,
            IsBoardDirty = () => false,
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.NotNull(vm.PendingImport);
        Assert.Single(vm.PendingImport!.AppliedTableSummaries);
        Assert.Empty(vm.PendingImport.NotImported);
    }

    [Fact]
    public async Task EnsureMetadataAsync_CalledTwiceForSameTable_FetchesOnce()
    {
        var generate = MakeGenerateViewModel(out var metadataMock);
        metadataMock
            .Setup(m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityMetadata { LogicalName = "account" });

        await generate.EnsureMetadataAsync(["account"], TestContext.Current.CancellationToken);
        await generate.EnsureMetadataAsync(["account"], TestContext.Current.CancellationToken);

        metadataMock.Verify(
            m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.True(generate.EntityMetadataMap.ContainsKey("account"));
    }

    [Fact]
    public async Task EnsureMetadataAsync_ProviderFails_PropagatesRatherThanReturningEmptyMap()
    {
        var generate = MakeGenerateViewModel(out var metadataMock);
        metadataMock
            .Setup(m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("org unreachable"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generate.EnsureMetadataAsync(["account"], TestContext.Current.CancellationToken));

        Assert.Empty(generate.EntityMetadataMap);
    }

    [Fact]
    public async Task EnsureMetadataAsync_PartialFailure_KeepsResolvedTablesAndSkipsMissing()
    {
        var generate = MakeGenerateViewModel(out var metadataMock);
        metadataMock
            .Setup(m => m.GetEntityAsync("account", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityMetadata { LogicalName = "account" });
        metadataMock
            .Setup(m => m.GetEntityAsync("bogus_table", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("table not found in environment"));

        await generate.EnsureMetadataAsync(
            ["account", "bogus_table"], TestContext.Current.CancellationToken);

        Assert.True(generate.EntityMetadataMap.ContainsKey("account"));
        Assert.False(generate.EntityMetadataMap.ContainsKey("bogus_table"));
    }
}

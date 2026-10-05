using SeedBomb.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using SeedBomb.Core.Exceptions;
using System.ServiceModel;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using SeedBomb.Services.Generation;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels;
using SeedBomb.Views.Pages;
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
    public async Task LoadAsync_WithMetadataHost_ProducesPendingImport()
    {
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Sales" });
        profiles.Setup(p => p.LoadAsync("Sales", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProfile("Sales"));

        var vm = ProfilesHost.Create(profiles.Object, board: ProfilesHost.Board(runId: "run-1").Object);
        vm.ConfirmOverwrite = _ => true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.NotNull(vm.PendingImport);
    }

    [Fact]
    public async Task OpenInBoard_applies_the_report_to_the_board_and_opens_Generate()
    {
        // WR-001: this hop lived in ProfilesPage code-behind (ProfileApplied → ApplyImportReport + Navigate).
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Sales" });
        profiles.Setup(p => p.LoadAsync("Sales", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProfile("Sales"));
        var board = ProfilesHost.Board(
            new Dictionary<string, EntityMetadata> { ["account"] = new() { LogicalName = "account" } }, runId: "run-1");
        var navigator = new Mock<IAppNavigator>();
        var vm = ProfilesHost.Create(profiles.Object, navigator: navigator.Object, board: board.Object);
        vm.ConfirmOverwrite = _ => true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();
        await vm.LoadCommand.ExecuteAsync(null);
        var report = vm.PendingImport;

        Assert.True(vm.OpenInBoardCommand.CanExecute(null));
        vm.OpenInBoardCommand.Execute(null);

        board.Verify(b => b.ApplyImportReport(report!), Times.Once);
        navigator.Verify(n => n.Navigate(typeof(GeneratePage)), Times.Once);
        Assert.False(vm.ShowImportSummary);
    }

    [Fact]
    public async Task OpenInBoard_IsDisabledWhenNoTableIsInThisOrg()
    {
        // The board would get only the profile name and seed; Not imported already lists the tables.
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "Sales" });
        profiles.Setup(p => p.LoadAsync("Sales", It.IsAny<CancellationToken>())).ReturnsAsync(MakeProfile("Sales"));
        var vm = ProfilesHost.Create(profiles.Object); // empty metadata map: "account" isn't in this org
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.True(vm.ShowImportSummary);
        Assert.False(vm.OpenInBoardCommand.CanExecute(null));
        Assert.True(vm.DiscardImportCommand.CanExecute(null));
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

        var vm = ProfilesHost.Create(profiles.Object, board: generate);
        vm.ConfirmOverwrite = _ => true;
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

    [Fact]
    public async Task Load_ProfileWhoseTablesAreAllMissing_ListsEachInNotImported()
    {
        // Smoke item 7: event-profile on an org with ryan_event instead of test_event surfaced
        // "Could not find an entity with name test_event…" instead of the import summary.
        var generate = MakeGenerateViewModel(out var metadataMock);
        metadataMock
            .Setup(m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((name, _) => Task.FromException<EntityMetadata>(MissingTable(name)));
        var profile = new Profile(Profile.CurrentProfileVersion, "event-profile", null, 7,
            [new ProfileTable("test_event", 10, null), new ProfileTable("test_eventattendance", 10, null)]);
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "event-profile" });
        profiles.Setup(p => p.LoadAsync("event-profile", It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        var vm = ProfilesHost.Create(profiles.Object, board: generate);
        vm.ConfirmOverwrite = _ => true;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedItem = vm.Items.Single();

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.HasError, vm.StatusMessage);
        Assert.Equal(
            ["test_event — table not available in this environment.",
             "test_eventattendance — table not available in this environment."],
            vm.PendingImport!.NotImported);
    }

    // The shape DataverseMetadataProvider throws for RetrieveEntity on a table the org lacks.
    private static SchemaException MissingTable(string name) => new(
        $"Failed to retrieve metadata for entity '{name}': Could not find an entity with name {name}",
        new FaultException<OrganizationServiceFault>(new OrganizationServiceFault
        {
            ErrorCode = unchecked((int)0x80040217),
            Message = $"Could not find an entity with name {name}",
        }));
}

using DataGen.Core.Contracts;
using Seedbomb.Services.Generation;
using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests;

/// <summary>
/// The link phase must keep the sheet honest: record creation finishing is not the run
/// finishing, so Linking snapshots drive a secondary bar without disturbing the ring.
/// </summary>
public sealed class RunViewModelLinkPhaseTests
{
    [Fact]
    public void AcceptProgress_LinkingSnapshot_DrivesSecondaryBar_AndLeavesRingAlone()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 100, tables: ["account"]);
        vm.AcceptProgress(
            new ProgressUpdate("Generating", "account", 80, 100, 4, 5, 600, TimeSpan.FromSeconds(8)),
            ["account"], 100);
        var percentBeforeLink = vm.OverallPercent;
        var tableRowsBeforeLink = vm.Tables.Count;

        vm.AcceptProgress(
            new ProgressUpdate("Linking", "contact", 2, 5, 0, 0, 0, TimeSpan.FromSeconds(9)),
            ["account"], 100);

        Assert.True(vm.IsLinking);
        Assert.Equal(40, vm.LinkPercent);
        Assert.Equal("Linking records…", vm.StatusHeadline);
        Assert.Equal(percentBeforeLink, vm.OverallPercent);
        // A link snapshot names a source entity but is not record creation: no table row for it.
        Assert.Equal(tableRowsBeforeLink, vm.Tables.Count);
        Assert.DoesNotContain(vm.Tables, t => t.TableName == "contact");
    }

    [Fact]
    public void AcceptProgress_OpeningLinkingSnapshot_IsIndeterminate()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 100, tables: ["account"]);
        vm.AcceptProgress(
            new ProgressUpdate("Generating", "account", 100, 100, 2, 2, 600, TimeSpan.FromSeconds(8)),
            ["account"], 100);
        Assert.False(vm.IsIndeterminate);

        vm.AcceptProgress(
            new ProgressUpdate("Linking", "", 0, 0, 0, 0, 0, TimeSpan.FromSeconds(9)),
            ["account"], 100);

        Assert.True(vm.IsLinking);
        Assert.True(vm.IsIndeterminate);
        Assert.Equal("Preparing links…", vm.LinkLabel);
        Assert.Equal(0, vm.LinkPercent);
    }

    [Fact]
    public void ApplyResult_ClearsLinkingState()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 10, tables: ["account"]);
        vm.AcceptProgress(
            new ProgressUpdate("Linking", "contact", 2, 5, 0, 0, 0, TimeSpan.FromSeconds(9)),
            ["account"], 10);
        Assert.True(vm.IsLinking);

        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [Guid.NewGuid()],
            },
            Elapsed = TimeSpan.FromSeconds(10),
            Errors = [],
        }, seed: 1, environmentHost: "contoso-dev");

        Assert.False(vm.IsLinking);
    }
}

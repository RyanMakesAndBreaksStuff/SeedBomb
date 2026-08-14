using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void UserInitialsTakesFirstLettersOfTwoNames()
    {
        var vm = new MainWindowViewModel();
        vm.UserDisplayName = "Marcus Kade";
        Assert.Equal("MK", vm.UserInitials);
    }

    [Fact]
    public void EnvironmentHostIsOrgHostAlias()
    {
        var vm = new MainWindowViewModel();
        string? last = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.EnvironmentHost))
                last = vm.EnvironmentHost;
        };
        vm.OrgUrl = "https://contoso-dev.crm.dynamics.com/";
        Assert.Equal("contoso-dev.crm.dynamics.com", vm.OrgHost);
        Assert.Equal(vm.OrgHost, vm.EnvironmentHost);
        Assert.Equal(vm.OrgHost, last);
    }

    [Fact]
    public void OpenConnectionsCommandIsOpenConnectionManagerCommand()
    {
        var vm = new MainWindowViewModel();
        Assert.Same(vm.OpenConnectionManagerCommand, vm.OpenConnectionsCommand);
    }
}

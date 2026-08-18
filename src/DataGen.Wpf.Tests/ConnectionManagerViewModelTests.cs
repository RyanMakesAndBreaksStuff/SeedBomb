using Moq;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Dataverse;
using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class ConnectionManagerViewModelTests
{
    [Fact]
    public async Task TestConnectionDoesNotPersistAndUsesParentHwnd()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.TryConnectAsync(It.IsAny<ConnectionProfile>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResult(true, "user@contoso.com", null));

        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, Mock.Of<IDataverseConnectionService>())
        {
            ParentHwnd = 42,
            EditingProfile = new ConnectionProfile
            {
                Name = "draft",
                EnvironmentUrl = "https://org.crm.dynamics.com",
                ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
            },
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.TestSucceeded);
        Assert.Contains("user@contoso.com", vm.TestResult, StringComparison.Ordinal);
        profiles.Verify(p => p.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()), Times.Never);
        profiles.Verify(p => p.SetLastUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        auth.Verify(
            a => a.TryConnectAsync(vm.EditingProfile!, 42, It.IsAny<CancellationToken>()),
            Times.Once);
        auth.Verify(a => a.SignInAsync(It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestConnectionAsync_PassesParentHwndToAuthService()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var auth = new Mock<IAuthService>();
        var connections = new Mock<IDataverseConnectionService>();

        nint captured = -1;
        auth.Setup(a => a.TryConnectAsync(
                It.IsAny<ConnectionProfile>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .Callback<ConnectionProfile, nint, CancellationToken>((_, hwnd, _) => captured = hwnd)
            .ReturnsAsync(new AuthResult(true, "user@contoso.com", null));

        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, connections.Object)
        {
            EditingProfile = new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://c.crm.dynamics.com" },
            ParentHwnd = (nint)4242,
        };

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal((nint)4242, captured);
    }

    [Fact]
    public async Task SaveProfileFailureSetsSwitchErrorAndStaysEditing()
    {
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Security.Cryptography.CryptographicException("dpapi"));

        var vm = new ConnectionManagerViewModel(profiles.Object, Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>())
        {
            EditingProfile = new ConnectionProfile { Name = "x" },
            IsEditing = true,
        };

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal("dpapi", vm.SwitchError);
        Assert.True(vm.HasSwitchError);
    }

    [Fact]
    public async Task SelectProfileResetsConnectionOnSuccess()
    {
        var profile = new ConnectionProfile
        {
            Name = "org-b",
            EnvironmentUrl = "https://orgb.crm.dynamics.com",
            ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
        };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([profile]);
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.SignInAsync(It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResult(true, "user@contoso.com", null));

        var connection = new Mock<IDataverseConnectionService>();
        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, connection.Object);
        var switched = false;
        vm.ConnectionSwitched += (_, _) => switched = true;

        await vm.SelectProfileCommand.ExecuteAsync(profile);

        Assert.True(switched);
        connection.Verify(c => c.Reset(), Times.Once);
        profiles.Verify(p => p.SetLastUsedAsync(profile.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void IsActiveAliasesIsLastUsed()
    {
        var profile = new ConnectionProfile { IsLastUsed = true };
        Assert.True(profile.IsActive);
        profile.IsActive = false;
        Assert.False(profile.IsLastUsed);
    }

    [Fact]
    public void UseDefaultClientIdWritesWellKnownPublicClient()
    {
        var vm = new ConnectionManagerViewModel(
            Mock.Of<IConnectionProfileService>(),
            Mock.Of<IAuthService>(),
            Mock.Of<IDataverseConnectionService>())
        {
            EditingProfile = new ConnectionProfile { ClientId = "old" },
        };

        vm.UseDefaultClientIdCommand.Execute(null);
        Assert.Equal("51f81489-12ee-4a9e-aaae-a2591f45987d", vm.EditingProfile!.ClientId);
    }

    [Fact]
    public void CancelEditCommandIsCancelCommand()
    {
        var vm = new ConnectionManagerViewModel(
            Mock.Of<IConnectionProfileService>(),
            Mock.Of<IAuthService>(),
            Mock.Of<IDataverseConnectionService>());
        Assert.Same(vm.CancelCommand, vm.CancelEditCommand);
    }

    [Fact]
    public async Task DeleteProfileAsync_LeavesProfilesEmpty_WhenLastProfileRemoved()
    {
        var stored = new List<ConnectionProfile>
        {
            new() { Name = "Only", EnvironmentUrl = "https://c.crm.dynamics.com" },
        };

        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored.ToList());
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored.FirstOrDefault());
        profiles.Setup(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) => stored.RemoveAll(p => p.Id == id))
            .Returns(Task.CompletedTask);

        var vm = new ConnectionManagerViewModel(
            profiles.Object, new Mock<IAuthService>().Object, new Mock<IDataverseConnectionService>().Object);

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Single(vm.Profiles);

        await vm.DeleteProfileCommand.ExecuteAsync(vm.Profiles[0]);

        Assert.Empty(vm.Profiles);
    }

    [Theory]
    [InlineData("", "https://c.crm.dynamics.com", "51f81489-12ee-4a9e-aaae-a2591f45987d")]
    [InlineData("Dev", "", "51f81489-12ee-4a9e-aaae-a2591f45987d")]
    [InlineData("Dev", "not-a-url", "51f81489-12ee-4a9e-aaae-a2591f45987d")]
    [InlineData("Dev", "https://c.crm.dynamics.com", "")]
    public void SaveProfileCommand_CannotExecute_ForIncompleteProfiles(string name, string url, string clientId)
    {
        var vm = new ConnectionManagerViewModel(
            new Mock<IConnectionProfileService>().Object,
            new Mock<IAuthService>().Object,
            new Mock<IDataverseConnectionService>().Object)
        {
            EditingProfile = new ConnectionProfile { Name = name, EnvironmentUrl = url, ClientId = clientId },
        };

        Assert.False(vm.SaveProfileCommand.CanExecute(null));
    }

    [Fact]
    public void SaveProfileCommand_CanExecute_ForACompleteProfile()
    {
        var vm = new ConnectionManagerViewModel(
            new Mock<IConnectionProfileService>().Object,
            new Mock<IAuthService>().Object,
            new Mock<IDataverseConnectionService>().Object)
        {
            EditingProfile = new ConnectionProfile
            {
                Name = "Dev",
                EnvironmentUrl = "https://contoso.crm.dynamics.com",
                ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
            },
        };

        Assert.True(vm.SaveProfileCommand.CanExecute(null));
    }
}

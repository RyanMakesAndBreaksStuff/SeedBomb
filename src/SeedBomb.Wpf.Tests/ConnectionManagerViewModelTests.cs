using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class ConnectionManagerViewModelTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

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
        auth.Verify(a => a.SignInAsync(It.IsAny<ConnectionProfile>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Never);
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
        auth.Setup(a => a.SignInAsync(It.IsAny<ConnectionProfile>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResult(true, "user@contoso.com", null));

        var connection = new Mock<IDataverseConnectionService>();
        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, connection.Object);
        var switched = false;
        vm.ConnectionSwitched += (_, _) => switched = true;

        await vm.SelectProfileCommand.ExecuteAsync(profile);

        Assert.True(switched);
        connection.Verify(c => c.ResetAsync(), Times.Once);
        auth.Verify(a => a.SignInAsync(profile, It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(profile.Id, vm.ConnectedProfileId);
    }

    [Fact]
    public async Task SelectProfileAsync_LeavesConnectedProfileIdUnchanged_OnFailure()
    {
        var profile = new ConnectionProfile { Name = "org-b", EnvironmentUrl = "https://orgb.crm.dynamics.com" };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([profile]);
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.SignInAsync(It.IsAny<ConnectionProfile>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResult(false, null, "bad credentials"));

        var vm = new ConnectionManagerViewModel(
            profiles.Object, auth.Object, Mock.Of<IDataverseConnectionService>());

        await vm.SelectProfileCommand.ExecuteAsync(profile);

        Assert.Null(vm.ConnectedProfileId);
        Assert.Equal("bad credentials", vm.SwitchError);
    }

    [Fact]
    public void IsLastUsed_RoundTrips()
    {
        var profile = new ConnectionProfile { IsLastUsed = true };
        Assert.True(profile.IsLastUsed);
        profile.IsLastUsed = false;
        Assert.False(profile.IsLastUsed);
    }

    [Fact]
    public async Task EditProfile_ClonesCertificateThumbprintAndClientSecret()
    {
        var stored = new ConnectionProfile
        {
            Name = "cert",
            EnvironmentUrl = "https://c.crm.dynamics.com",
            ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
            AuthType = AuthType.Certificate,
            CertificateThumbprint = "ABC123",
            ClientSecret = "s3cret",
        };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetSecretAsync(stored.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync("s3cret");
        var vm = new ConnectionManagerViewModel(
            profiles.Object,
            Mock.Of<IAuthService>(),
            Mock.Of<IDataverseConnectionService>());

        await vm.EditProfileCommand.ExecuteAsync(stored);

        Assert.NotSame(stored, vm.EditingProfile);
        Assert.Equal(AuthType.Certificate, vm.EditingProfile!.AuthType);
        Assert.Equal("ABC123", vm.EditingProfile.CertificateThumbprint);
        Assert.Equal("s3cret", vm.EditingProfile.ClientSecret);
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
    public void UseDefaultClientIdCommand_RaisesCanExecuteChangedForSaveProfile()
    {
        var vm = new ConnectionManagerViewModel(
            Mock.Of<IConnectionProfileService>(),
            Mock.Of<IAuthService>(),
            Mock.Of<IDataverseConnectionService>())
        {
            EditingProfile = new ConnectionProfile
            {
                Name = "Dev",
                EnvironmentUrl = "https://contoso.crm.dynamics.com",
                ClientId = string.Empty,
            },
        };

        Assert.False(vm.SaveProfileCommand.CanExecute(null));

        var raised = false;
        vm.SaveProfileCommand.CanExecuteChanged += (_, _) => raised = true;

        vm.UseDefaultClientIdCommand.Execute(null);

        Assert.True(raised);
        Assert.True(vm.SaveProfileCommand.CanExecute(null));
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

    [Fact]
    public async Task DeleteProfileAsync_ClearsConnectedProfileId_WhenDeletingTheConnectedProfile()
    {
        var stored = new List<ConnectionProfile>
        {
            new() { Name = "Only", EnvironmentUrl = "https://c.crm.dynamics.com" },
        };
        var target = stored[0];

        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => stored.ToList());
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => stored.FirstOrDefault());
        profiles.Setup(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) => stored.RemoveAll(p => p.Id == id))
            .Returns(Task.CompletedTask);

        var vm = new ConnectionManagerViewModel(
            profiles.Object, Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>())
        {
            ConnectedProfileId = target.Id,
        };

        await vm.DeleteProfileCommand.ExecuteAsync(target);

        Assert.Null(vm.ConnectedProfileId);
    }

    [Fact]
    public async Task DeleteProfileAsync_ClearsEditingProfile_WhenDeletingTheEditedProfile()
    {
        var stored = new List<ConnectionProfile>
        {
            new() { Name = "Only", EnvironmentUrl = "https://c.crm.dynamics.com" },
        };
        var target = stored[0];

        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => stored.ToList());
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => stored.FirstOrDefault());
        profiles.Setup(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) => stored.RemoveAll(p => p.Id == id))
            .Returns(Task.CompletedTask);

        var vm = new ConnectionManagerViewModel(
            profiles.Object, Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>());
        vm.EditProfileCommand.Execute(target);
        Assert.True(vm.IsEditing);

        await vm.DeleteProfileCommand.ExecuteAsync(target);

        Assert.Null(vm.EditingProfile);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task DeleteProfileAsync_DialogHostUnavailable_CancelsDeleteInsteadOfProceeding()
    {
        var stored = new ConnectionProfile { Name = "Prod", EnvironmentUrl = "https://c.crm.dynamics.com" };

        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([stored]);
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ConnectionProfile?)null);

        // Simulates a dialog host that isn't registered yet (e.g. shown before MainWindow loads).
        var dialogs = new Mock<IContentDialogService>();
        dialogs.Setup(d => d.ShowAsync(It.IsAny<ContentDialog>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no dialog host registered"));

        var vm = new ConnectionManagerViewModel(
            profiles.Object, Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>(), dialogs.Object);

        await vm.DeleteProfileCommand.ExecuteAsync(stored);

        // Whichever step fails first (off-STA dialog construction, or the mocked ShowAsync),
        // the point of the fail-safe is: no dialog shown correctly => no delete.
        profiles.Verify(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(vm.HasSwitchError);
        Assert.NotNull(vm.SwitchError);
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

    [Theory]
    [InlineData(AuthType.OAuth, null, null, true)]
    [InlineData(AuthType.OAuth, "", "", true)]
    [InlineData(AuthType.ClientSecret, null, null, false)]
    [InlineData(AuthType.ClientSecret, "", null, false)]
    [InlineData(AuthType.ClientSecret, "   ", null, false)]
    [InlineData(AuthType.ClientSecret, "s3cret", null, true)]
    [InlineData(AuthType.Certificate, null, null, false)]
    [InlineData(AuthType.Certificate, null, "", false)]
    [InlineData(AuthType.Certificate, null, "   ", false)]
    [InlineData(AuthType.Certificate, null, "ABC123", true)]
    public void SaveProfileCommand_RequiresCredentialMatchingAuthType(
        AuthType authType, string? clientSecret, string? certificateThumbprint, bool expectedCanSave)
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
                AuthType = authType,
                ClientSecret = clientSecret,
                CertificateThumbprint = certificateThumbprint,
            },
        };

        Assert.Equal(expectedCanSave, vm.SaveProfileCommand.CanExecute(null));
    }

    [Fact]
    public void NewProfileCommand_ShowsSaveButton_NotConnectButton()
    {
        var vm = new ConnectionManagerViewModel(
            Mock.Of<IConnectionProfileService>(), Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>());

        vm.NewProfileCommand.Execute(null);

        Assert.True(vm.ShowSaveButton);
        Assert.False(vm.ShowConnectButton);
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveProfileCommand_Success_SwitchesToConnectButton_WithoutAutoConnecting()
    {
        var profiles = new Mock<IConnectionProfileService>();
        var auth = new Mock<IAuthService>();
        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, Mock.Of<IDataverseConnectionService>());
        vm.NewProfileCommand.Execute(null);
        vm.EditingProfile!.Name = "Dev";
        vm.EditingProfile!.EnvironmentUrl = "https://contoso.crm.dynamics.com";

        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([vm.EditingProfile]);
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ConnectionProfile?)null);

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.False(vm.ShowSaveButton);
        Assert.True(vm.ShowConnectButton);
        profiles.Verify(p => p.SetLastUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        auth.Verify(a => a.SignInAsync(It.IsAny<ConnectionProfile>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditingProfile_MarkedDirtyAfterSave_SwitchesBackToSaveButton()
    {
        var profile = new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://contoso.crm.dynamics.com" };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([profile]);
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ConnectionProfile?)null);

        var vm = new ConnectionManagerViewModel(profiles.Object, Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>());
        await vm.LoadCommand.ExecuteAsync(null);
        vm.EditProfileCommand.Execute(profile);
        Assert.True(vm.ShowConnectButton);

        vm.IsDirty = true;

        Assert.True(vm.ShowSaveButton);
        Assert.False(vm.ShowConnectButton);
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConnectAsync_RaisesConnectionSwitched_AndShowsThenHidesToast()
    {
        var profile = new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://contoso.crm.dynamics.com" };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([profile]);
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.SignInAsync(It.IsAny<ConnectionProfile>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResult(true, "user@contoso.com", null));

        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, Mock.Of<IDataverseConnectionService>())
        {
            ConnectedToastDuration = TimeSpan.Zero,
        };
        await vm.LoadCommand.ExecuteAsync(null);
        vm.EditProfileCommand.Execute(profile);
        Assert.True(vm.ShowConnectButton);

        var switched = false;
        vm.ConnectionSwitched += (_, _) => switched = true;
        var toastShown = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ShowConnectedToast) && vm.ShowConnectedToast)
                toastShown = true;
        };

        await vm.ConnectCommand.ExecuteAsync(null);

        // The toast is shown/hidden by a fire-and-forget task (ConnectCommand doesn't wait out
        // its display time) — await the test seam that tracks it before asserting it hid.
        Assert.NotNull(vm.ConnectedToastTask);
        await vm.ConnectedToastTask!;

        Assert.True(switched);
        Assert.True(toastShown);
        Assert.False(vm.ShowConnectedToast);
    }

    [Theory]
    [InlineData(nameof(ConnectionProfile.TenantId), "8f4a1c22-0000-4a00-9000-2f0e0e0a1111")]
    [InlineData(nameof(ConnectionProfile.Name), "renamed")]
    [InlineData(nameof(ConnectionProfile.EnvironmentUrl), "https://other.crm.dynamics.com")]
    [InlineData(nameof(ConnectionProfile.ClientId), "51f81489-12ee-4a9e-aaae-a2591f45987d")]
    public async Task EditingASavedProfileField_ShowsSaveButton(string property, string value)
    {
        var vm = await ViewModelWithOneSavedProfileAsync();
        vm.EditProfileCommand.Execute(vm.Profiles[0]);
        Assert.False(vm.ShowSaveButton);   // opened clean

        typeof(ConnectionProfile).GetProperty(property)!.SetValue(vm.EditingProfile, value);

        Assert.True(vm.IsDirty);
        Assert.True(vm.ShowSaveButton);
    }

    [Fact]
    public async Task OpeningAProfileForEdit_DoesNotMarkItDirty()
    {
        var vm = await ViewModelWithOneSavedProfileAsync();

        vm.EditProfileCommand.Execute(vm.Profiles[0]);

        Assert.False(vm.IsDirty);
        Assert.False(vm.ShowSaveButton);
        Assert.True(vm.ShowConnectButton);
    }

    private static async Task<ConnectionManagerViewModel> ViewModelWithOneSavedProfileAsync()
    {
        var profile = new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://contoso.crm.dynamics.com" };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([profile]);
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ConnectionProfile?)null);

        var vm = new ConnectionManagerViewModel(
            profiles.Object, Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>());
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task GetAllAsync_DoesNotReturnPlaintextSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (svc, id) = await StoreWithOneSecretProfileAsync();

        var all = await svc.GetAllAsync(ct);

        Assert.Null(Assert.Single(all).ClientSecret);
        Assert.Equal("s3cret", await svc.GetSecretAsync(id, ct));
    }

    [Fact]
    public async Task SaveAsync_WithNullSecret_KeepsTheStoredSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var (svc, id) = await StoreWithOneSecretProfileAsync();

        var listed = Assert.Single(await svc.GetAllAsync(ct));   // ClientSecret is null here
        listed.Name = "renamed";
        await svc.SaveAsync(listed, ct);

        Assert.Equal("s3cret", await svc.GetSecretAsync(id, ct));
    }

    [Fact]
    public async Task SaveAsync_KeepsTheSignedInAccountOfAnEditedProfile()
    {
        // WR-005: the account recorded at interactive sign-in must survive a save from the editor,
        // whose copy of the profile never carries it.
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "SeedBomb.Wpf.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var id = Guid.NewGuid();
        var path = Path.Combine(dir, "connections.json");
        await File.WriteAllTextAsync(path, $$"""
            { "Profiles": [{ "Id": "{{id}}", "Name": "Dev", "EnvironmentUrl": "https://dev.crm.dynamics.com",
              "AuthType": "OAuth", "ClientId": "51f81489-12ee-4a9e-aaae-a2591f45987d", "HomeAccountId": "uid.utid" }] }
            """, ct);
        var svc = new JsonConnectionProfileService(dir);

        await svc.SaveAsync(new ConnectionProfile
        {
            Id = id,
            Name = "Dev (renamed)",
            EnvironmentUrl = "https://dev.crm.dynamics.com",
            ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
        }, ct);

        Assert.Contains("\"HomeAccountId\": \"uid.utid\"", await File.ReadAllTextAsync(path, ct), StringComparison.Ordinal);
    }

    private async Task<(JsonConnectionProfileService svc, Guid id)> StoreWithOneSecretProfileAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SeedBomb.Wpf.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var svc = new JsonConnectionProfileService(dir);
        var profile = new ConnectionProfile
        {
            Name = "secret-profile",
            EnvironmentUrl = "https://org.crm.dynamics.com",
            ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
            AuthType = AuthType.ClientSecret,
            ClientSecret = "s3cret",
        };
        await svc.SaveAsync(profile, TestContext.Current.CancellationToken);
        return (svc, profile.Id);
    }

    [Fact]
    public async Task EditProfileAsync_ReportsSwitchError_WhenSecretCannotBeDecrypted()
    {
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetSecretAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Security.Cryptography.CryptographicException("Key not valid for use in specified state."));

        var vm = new ConnectionManagerViewModel(
            profiles.Object, Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>());

        var profile = new ConnectionProfile
        {
            Name = "Dev",
            EnvironmentUrl = "https://org.crm.dynamics.com",
            AuthType = AuthType.ClientSecret,
        };

        await vm.EditProfileCommand.ExecuteAsync(profile);

        Assert.Contains("Key not valid", vm.SwitchError, StringComparison.Ordinal);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task Failed_switch_leaves_last_used_and_the_live_connection_alone()
    {
        // CR-002: last-used used to be written before sign-in, so a cancelled switch aimed the next
        // connection at the new org while the header still showed the old one.
        var prod = new ConnectionProfile { Name = "PROD", EnvironmentUrl = "https://prod.crm.dynamics.com" };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([prod]);
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.SignInAsync(prod, It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResult(false, null, "User canceled authentication."));
        var connection = new Mock<IDataverseConnectionService>();
        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, connection.Object);

        await vm.SelectProfileCommand.ExecuteAsync(prod);

        profiles.Verify(p => p.SetLastUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        connection.Verify(c => c.ResetAsync(), Times.Never);
        Assert.Equal("User canceled authentication.", vm.SwitchError);
    }

    [Fact]
    public void Connection_changes_are_blocked_while_a_run_is_writing()
    {
        // WR-001: each of these can dispose the ServiceClient the running pipeline writes through.
        var profile = new ConnectionProfile
        {
            Name = "Dev",
            EnvironmentUrl = "https://contoso.crm.dynamics.com",
            ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
        };
        var run = new RunViewModel { IsRunning = true };
        var vm = new ConnectionManagerViewModel(
            Mock.Of<IConnectionProfileService>(), Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>(),
            run: run)
        {
            EditingProfile = profile,
        };

        Assert.False(vm.SaveProfileCommand.CanExecute(null));
        Assert.False(vm.DeleteProfileCommand.CanExecute(profile));
        Assert.False(vm.SelectProfileCommand.CanExecute(profile));

        var raised = false;
        vm.SaveProfileCommand.CanExecuteChanged += (_, _) => raised = true;
        run.IsRunning = false;

        Assert.True(raised);
        Assert.True(vm.SaveProfileCommand.CanExecute(null));
        Assert.True(vm.DeleteProfileCommand.CanExecute(profile));
        Assert.True(vm.SelectProfileCommand.CanExecute(profile));
    }

    [Fact]
    public async Task Only_deleting_the_connected_profile_resets_the_connection()
    {
        // WR-001: saves and deletes of other profiles used to drop the live connection too.
        var connected = new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://dev.crm.dynamics.com" };
        var other = new ConnectionProfile { Name = "Test", EnvironmentUrl = "https://test.crm.dynamics.com" };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([connected, other]);
        var connection = new Mock<IDataverseConnectionService>();
        var vm = new ConnectionManagerViewModel(profiles.Object, Mock.Of<IAuthService>(), connection.Object)
        {
            ConnectedProfileId = connected.Id,
        };

        await vm.DeleteProfileCommand.ExecuteAsync(other);
        connection.Verify(c => c.ResetAsync(), Times.Never);

        await vm.DeleteProfileCommand.ExecuteAsync(connected);
        connection.Verify(c => c.ResetAsync(), Times.Once);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}

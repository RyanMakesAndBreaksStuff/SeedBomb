using Moq;
using SeedBomb.Core.Contracts;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.Services.Generation;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels;
using SeedBomb.Wpf.Tests.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;
using static SeedBomb.Wpf.Tests.ConnectionManagerViewModelTests;

namespace SeedBomb.Wpf.Tests;

[Collection("RunSession")]
public sealed class ConnectionManagerViewModelTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public ConnectionManagerViewModelTests() => RunViewModelTests.UseInlineRetryNotifications();


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

        ConnectionProfile? active = null;
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile).Returns(() => active);
        auth.Setup(a => a.SignInAsync(It.IsAny<ConnectionProfile>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .Callback<ConnectionProfile, nint, CancellationToken>((p, _, _) => active = p)
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
    public void EditProfile_ClonesFieldsButLeavesTheSavedSecretEncrypted()
    {
        var stored = new ConnectionProfile
        {
            Name = "app",
            EnvironmentUrl = "https://c.crm.dynamics.com",
            ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
            TenantId = "8f4a1c22-0000-4a00-9000-2f0e0e0a1111",
            AuthType = AuthType.ClientSecret,
            CertificateThumbprint = "ABC123",
            HasSavedSecret = true,
        };
        var profiles = new Mock<IConnectionProfileService>();
        var vm = new ConnectionManagerViewModel(
            profiles.Object,
            Mock.Of<IAuthService>(),
            Mock.Of<IDataverseConnectionService>());

        vm.EditProfileCommand.Execute(stored);

        Assert.NotSame(stored, vm.EditingProfile);
        Assert.Equal("ABC123", vm.EditingProfile!.CertificateThumbprint);
        Assert.Null(vm.EditingProfile.ClientSecret);
        Assert.True(vm.SaveProfileCommand.CanExecute(null)); // the saved secret satisfies the check
        profiles.Verify(p => p.GetSecretAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
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
                TenantId = "8f4a1c22-0000-4a00-9000-2f0e0e0a1111",
                AuthType = authType,
                ClientSecret = clientSecret,
                CertificateThumbprint = certificateThumbprint,
            },
        };

        Assert.Equal(expectedCanSave, vm.SaveProfileCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(AuthType.ClientSecret, "")]
    [InlineData(AuthType.Certificate, "contoso.onmicrosoft.com")]
    public void SaveProfileCommand_BlocksAppOnlyProfileWithoutAGuidTenant(AuthType authType, string tenantId)
    {
        // WR-012: Save accepted these, then sign-in rejected them with "Tenant ID is not configured".
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
                TenantId = tenantId,
                ClientSecret = "s3cret",
                CertificateThumbprint = "ABC123",
            },
        };

        Assert.False(vm.SaveProfileCommand.CanExecute(null));
    }

    [Fact]
    public void ProfileError_NamesTheMissingTenant_OnceTheProfileIsEdited()
    {
        var vm = new ConnectionManagerViewModel(
            new Mock<IConnectionProfileService>().Object,
            new Mock<IAuthService>().Object,
            new Mock<IDataverseConnectionService>().Object);
        vm.NewProfileCommand.Execute(null);
        Assert.Null(vm.ProfileError); // an untouched form shows nothing

        vm.EditingProfile!.EnvironmentUrl = "https://contoso.crm.dynamics.com";
        vm.EditingProfile.AuthType = AuthType.ClientSecret;

        Assert.Contains("Tenant ID", vm.ProfileError, StringComparison.Ordinal);
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

        var listed = Assert.Single(all);
        Assert.Null(listed.ClientSecret);
        Assert.True(listed.HasSavedSecret);
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
        vm.Profiles.Add(profile); // a stored profile: Delete is gated on the run only

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
    public void DeleteConnection_NeedsASavedProfile()
    {
        // Smoke item 9: the empty editor bound a null EditingProfile and Delete stayed enabled.
        var vm = new ConnectionManagerViewModel(
            Mock.Of<IConnectionProfileService>(), Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>());

        Assert.False(vm.DeleteProfileCommand.CanExecute(null));

        vm.NewProfileCommand.Execute(null);
        Assert.False(vm.DeleteProfileCommand.CanExecute(vm.EditingProfile));

        vm.Profiles.Add(vm.EditingProfile!);
        Assert.True(vm.DeleteProfileCommand.CanExecute(vm.EditingProfile));
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
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile).Returns(connected);
        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, connection.Object);

        await vm.DeleteProfileCommand.ExecuteAsync(other);
        connection.Verify(c => c.ResetAsync(), Times.Never);

        await vm.DeleteProfileCommand.ExecuteAsync(connected);
        connection.Verify(c => c.ResetAsync(), Times.Once);
    }

    [Fact]
    public async Task Deleting_the_signed_in_profile_resets_the_connection_without_a_window_seeding_it()
    {
        // WR-003: the reset read ConnectedProfileId, a mirror only MainWindow code-behind seeded and
        // SignedOut could clear first. Startup's silent sign-in never wrote it at all.
        var prod = new ConnectionProfile { Name = "PROD", EnvironmentUrl = "https://prod.crm.dynamics.com" };
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([prod]);
        ConnectionProfile? active = prod;
        var auth = new Mock<IAuthService>();
        auth.SetupGet(a => a.ActiveProfile).Returns(() => active);
        profiles.Setup(p => p.DeleteAsync(prod.Id, It.IsAny<CancellationToken>()))
            .Callback(() => active = null) // ProfilesChanged → ReconcileSessionAsync drops the session
            .Returns(Task.CompletedTask);
        var connection = new Mock<IDataverseConnectionService>();
        var vm = new ConnectionManagerViewModel(profiles.Object, auth.Object, connection.Object);

        await vm.DeleteProfileCommand.ExecuteAsync(prod);

        connection.Verify(c => c.ResetAsync(), Times.Once);
    }

    [Fact]
    public async Task Cancel_and_NewProfile_clear_the_selection_so_reselecting_reopens_the_editor()
    {
        // WR-011: SelectedProfile stayed set, so clicking the same row raised no change.
        var vm = await ViewModelWithOneSavedProfileAsync();
        var saved = vm.Profiles[0];

        vm.SelectedProfile = saved;
        vm.CancelCommand.Execute(null);
        vm.SelectedProfile = saved;
        Assert.Equal(saved.Id, vm.EditingProfile?.Id);

        vm.NewProfileCommand.Execute(null);
        vm.SelectedProfile = saved;
        Assert.Equal(saved.Id, vm.EditingProfile?.Id);
    }

    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Run_WaitsForInFlightSwitchAndReset()
    {
        var gate = new RunSessionGate();
        var profileA = Env("A", "https://a.crm.dynamics.com");
        var profileB = Env("B", "https://b.crm.dynamics.com");
        var auth = new MutableAuth { ActiveProfile = profileA, CurrentUserDisplayName = "ada@a" };
        var signInEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSignIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        auth.SignInHandler = async (profile, _) =>
        {
            signInEntered.TrySetResult();
            await releaseSignIn.Task;
            auth.ActiveProfile = profile;
            auth.CurrentUserDisplayName = "ada@b";
            auth.RaiseChanged();
            return new AuthResult(true, "ada@b", null);
        };
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Mock<IDataverseConnectionService>();
        connection.Setup(c => c.ResetAsync()).Returns(async () =>
        {
            resetEntered.TrySetResult();
            await releaseReset.Task;
        });
        var profiles = ProfilesOf(profileA, profileB);
        var calls = 0;
        string? hostAtGenerate = null;
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref calls);
                hostAtGenerate = auth.ActiveProfile?.EnvironmentUrl;
                return Task.FromResult(new GenerationResult
                {
                    CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
                    Elapsed = TimeSpan.FromSeconds(1),
                });
            });
        var settings = ReadySettings();
        var run = new RunViewModel(
            generation: gen.Object, settings: settings.Object, auth: auth, sessionGate: gate);
        var vm = new ConnectionManagerViewModel(profiles.Object, auth, connection.Object, sessionGate: gate);

        var switching = vm.SelectProfileCommand.ExecuteAsync(profileB);
        await signInEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        var running = run.ExecuteAsync(AccountConfig(), TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(0, calls);
            Assert.False(run.IsRunning);
            releaseSignIn.TrySetResult();
            await resetEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Equal(0, calls);
            Assert.False(run.IsRunning);
        }
        finally
        {
            releaseSignIn.TrySetResult();
            releaseReset.TrySetResult();
        }

        await switching.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await running.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal(profileB.EnvironmentUrl, hostAtGenerate);
        Assert.Equal("b.crm.dynamics.com", run.EnvironmentLabel);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Switch_WaitsUntilGenerationFinishes(string outcome)
    {
        var gate = new RunSessionGate();
        var profileA = Env("A", "https://a.crm.dynamics.com");
        var profileB = Env("B", "https://b.crm.dynamics.com");
        var auth = new MutableAuth { ActiveProfile = profileA, CurrentUserDisplayName = "ada@a" };
        var signInEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSignIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        auth.SignInHandler = async (profile, _) =>
        {
            signInEntered.TrySetResult();
            await releaseSignIn.Task;
            if (outcome == "cancelled")
                throw new OperationCanceledException();
            if (outcome == "failed")
                return new AuthResult(false, null, "User canceled authentication.");
            auth.ActiveProfile = profile;
            auth.CurrentUserDisplayName = "ada@b";
            auth.RaiseChanged();
            return new AuthResult(true, "ada@b", null);
        };
        var resets = 0;
        var connection = new Mock<IDataverseConnectionService>();
        connection.Setup(c => c.ResetAsync()).Callback(() => Interlocked.Increment(ref resets)).Returns(Task.CompletedTask);
        var genEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                genEntered.TrySetResult();
                await releaseGen.Task;
                return new GenerationResult
                {
                    CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
                    Elapsed = TimeSpan.FromSeconds(1),
                };
            });
        var run = new RunViewModel(
            generation: gen.Object, settings: ReadySettings().Object, auth: auth, sessionGate: gate);
        var vm = new ConnectionManagerViewModel(
            ProfilesOf(profileA, profileB).Object, auth, connection.Object, sessionGate: gate);

        var running = run.ExecuteAsync(AccountConfig(), TestContext.Current.CancellationToken);
        Task? switching = null;
        try
        {
            await genEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            switching = vm.SelectProfileCommand.ExecuteAsync(profileB);
            await Task.Yield();
            Assert.False(signInEntered.Task.IsCompleted);
            Assert.Equal(0, resets);
        }
        finally
        {
            releaseGen.TrySetResult();
            releaseSignIn.TrySetResult();
        }

        await running.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.NotNull(switching);
        await switching!.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await signInEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Equal("a.crm.dynamics.com", run.EnvironmentLabel);
        Assert.Equal(outcome == "success" ? 1 : 0, resets);
        if (outcome == "failed")
            Assert.Equal("User canceled authentication.", vm.SwitchError);
        if (outcome == "cancelled")
            Assert.False(string.IsNullOrEmpty(vm.SwitchError));
        if (outcome != "success")
            Assert.Equal(profileA.Id, auth.ActiveProfile?.Id);
    }

    internal static ConnectionProfile Env(string name, string url) => new()
    {
        Name = name,
        EnvironmentUrl = url,
        ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d",
    };

    internal static GenerationConfig AccountConfig() => new()
    {
        EntityLogicalNames = ["account"],
        RecordCounts = new Dictionary<string, int> { ["account"] = 1 },
        Seed = 7,
    };

    internal static Mock<ISettingsService> ReadySettings()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AppSettings.Default);
        return settings;
    }

    private static Mock<IConnectionProfileService> ProfilesOf(params ConnectionProfile[] profiles)
    {
        var mock = new Mock<IConnectionProfileService>();
        mock.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profiles);
        mock.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profiles.FirstOrDefault());
        return mock;
    }

    internal sealed class MutableAuth : IAuthService
    {
        public ConnectionProfile? ActiveProfile { get; set; }
        public string? CurrentUserDisplayName { get; set; }
        public Func<ConnectionProfile, CancellationToken, Task<AuthResult>>? SignInHandler { get; set; }
        public Func<CancellationToken, Task>? SignOutHandler { get; set; }
        public event EventHandler? SignedOut;
        public event EventHandler? ActiveProfileChanged;

        public Task<AuthResult> SignInAsync(nint parentHwnd, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult(false, null, "not used"));

        public Task<AuthResult> SignInAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default) =>
            SignInHandler is null
                ? Task.FromResult(new AuthResult(true, CurrentUserDisplayName, null))
                : SignInHandler(profile, ct);

        public Task<AuthResult> TryConnectAsync(ConnectionProfile profile, nint parentHwnd, CancellationToken ct = default) =>
            Task.FromResult(new AuthResult(false, null, null));

        public Task SignOutAsync(CancellationToken ct = default)
        {
            if (SignOutHandler is not null)
                return SignOutHandler(ct);
            SignedOut?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task ForgetProfileAsync(ConnectionProfile profile, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetTokenAsync(string[] scopes, CancellationToken ct = default) => Task.FromResult("token");

        public void RaiseChanged() => ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        RunViewModel.UiDispatcher = null;
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}

/// <summary>
/// Kept apart from <see cref="ConnectionManagerViewModelTests"/> because the StaUi collection must stay
/// on one STA thread (the shared <c>Application</c> belongs to it): the test body is run to completion
/// on the STA thread instead of letting its awaits move the collection runner to a pool thread.
/// </summary>
[Collection("StaUi")]
public sealed class ConnectionManagerSessionStaTests
{
    [StaFact]
    public void DeletingActiveProfile_ClearsSessionBeforeRunCanStart() =>
#pragma warning disable xUnit1031 // intentional: keep the collection runner on this STA thread
        DeletingActiveProfileAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031

    private static async Task DeletingActiveProfileAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SEEDBOMB_SKIP_GATE_UI"), "1", StringComparison.Ordinal))
            return;
        var gate = new RunSessionGate();
        var active = Env("Dev", "https://dev.crm.dynamics.com");
        var auth = new MutableAuth { ActiveProfile = active, CurrentUserDisplayName = "ada" };
        var releaseReconcile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconcileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionProfile>());
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ConnectionProfile?)null);
        profiles.Setup(p => p.DeleteAsync(active.Id, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                reconcileStarted.TrySetResult();
                return releaseReconcile.Task;
            });
        var inConfirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int? acquiredDuringConfirm = null;
        var dialogs = new Mock<IContentDialogService>();
        dialogs.Setup(d => d.ShowAsync(It.IsAny<ContentDialog>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var probe = gate.AcquireAsync(TestContext.Current.CancellationToken);
                acquiredDuringConfirm = probe.IsCompletedSuccessfully ? 1 : 0;
                if (probe.IsCompletedSuccessfully)
                    probe.Result.Dispose();
                inConfirm.TrySetResult();
                await releaseConfirm.Task;
                return ContentDialogResult.Primary;
            });
        var heldDuringSignOut = false;
        auth.SignOutHandler = async _ =>
        {
            using var probeCts = new CancellationTokenSource();
            var probe = gate.AcquireAsync(probeCts.Token);
            heldDuringSignOut = !probe.IsCompleted;
            if (probe.IsCompletedSuccessfully)
                probe.Result.Dispose();
            else
            {
                probeCts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
            }

            auth.ActiveProfile = null;
            auth.CurrentUserDisplayName = null;
            auth.RaiseChanged();
        };
        var calls = 0;
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(), It.IsAny<IProgress<ProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new GenerationResult { Elapsed = TimeSpan.FromSeconds(1) });
            });
        var run = new RunViewModel(
            generation: gen.Object, settings: ReadySettings().Object, auth: auth, sessionGate: gate);
        var vm = new ConnectionManagerViewModel(
            profiles.Object, auth, Mock.Of<IDataverseConnectionService>(), dialogs.Object, sessionGate: gate);

        var deleting = vm.DeleteProfileCommand.ExecuteAsync(active);
        try
        {
            await inConfirm.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Equal(1, acquiredDuringConfirm);
        }
        finally
        {
            releaseConfirm.TrySetResult();
            releaseReconcile.TrySetResult();
        }

        await deleting.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.True(reconcileStarted.Task.IsCompleted);
        Assert.True(heldDuringSignOut);
        Assert.Null(auth.ActiveProfile);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            run.ExecuteAsync(AccountConfig(), TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }
}

using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.ViewModels;
using Xunit;

namespace SeedBomb.Wpf.Tests;

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

    [Fact]
    public async Task HasConnection_FollowsTheProfileCount()
    {
        var (vm, store) = await MainWindowViewModelWithProfilesAsync(count: 1);
        Assert.True(vm.HasConnection);

        store.ReplaceAll([]);              // raises ProfilesChanged with an empty store
        await vm.PendingProfileSyncTask!;

        Assert.False(vm.HasConnection);
    }

    [Fact]
    public async Task ConnectionSwitched_UpdatesOrgUrlAndRaisesTheReloadEvent()
    {
        var (vm, _) = await MainWindowViewModelWithProfilesAsync(count: 1);
        var reloads = 0;
        vm.ConnectionReloadRequested += (_, _) => reloads++;

        vm.OnConnectionSwitched(
            new ConnectionProfile { EnvironmentUrl = "https://contoso-uat.crm.dynamics.com" },
            new AuthResult(true, "Ryan", null));

        Assert.Equal("contoso-uat.crm.dynamics.com", vm.OrgHost);
        Assert.False(vm.NeedsSignIn);
        Assert.Equal(1, reloads);
    }

    private static async Task<(MainWindowViewModel vm, FakeConnectionProfileStore store)>
        MainWindowViewModelWithProfilesAsync(int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeConnectionProfileStore();
        for (var i = 0; i < count; i++)
        {
            store.Seed(new ConnectionProfile
            {
                Name = $"p{i}",
                EnvironmentUrl = "https://org.crm.dynamics.com",
            });
        }

        var connectionManager = new ConnectionManagerViewModel(
            store,
            Mock.Of<IAuthService>(),
            Mock.Of<IDataverseConnectionService>());

        var vm = new MainWindowViewModel(store, connectionManager);
        await connectionManager.LoadAsync(ct);
        vm.SyncHasConnection();
        return (vm, store);
    }

    private sealed class FakeConnectionProfileStore : IConnectionProfileService
    {
        private List<ConnectionProfile> _profiles = [];

        public event EventHandler? ProfilesChanged;

        public void Seed(ConnectionProfile profile) => _profiles.Add(profile);

        public void ReplaceAll(IReadOnlyList<ConnectionProfile> profiles)
        {
            _profiles = [.. profiles];
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.ToList());
        }

        public Task SaveAsync(ConnectionProfile profile, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var idx = _profiles.FindIndex(p => p.Id == profile.Id);
            if (idx >= 0) _profiles[idx] = profile;
            else _profiles.Add(profile);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _profiles.RemoveAll(p => p.Id == id);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<ConnectionProfile?> GetLastUsedAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_profiles.FirstOrDefault());
        }

        public Task<string?> GetSecretAsync(Guid id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_profiles.FirstOrDefault(p => p.Id == id)?.ClientSecret);
        }

        public Task SetLastUsedAsync(Guid id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}

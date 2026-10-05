using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.ViewModels;
using Xunit;

namespace SeedBomb.Wpf.Tests;

/// <summary>CR-006: an unreadable connections.json or secret must never stop the app starting.</summary>
public sealed class ConnectionStoreRecoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "SeedBomb.Wpf.Tests", Guid.NewGuid().ToString("N"))).FullName;

    private string StorePath => Path.Combine(_dir, "connections.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Failed_write_does_not_corrupt_the_in_memory_cache()
    {
        // WR-007: PersistAsync used to assign `_cache = store` — the SAME object SaveAsync had
        // already mutated in place — before the disk write ran, so a failed write left the cache
        // showing the unsaved change forever, disagreeing with the file on disk.
        var ct = TestContext.Current.CancellationToken;
        var svc = new JsonConnectionProfileService(_dir);
        var profile = new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://dev.crm.dynamics.com" };
        await svc.SaveAsync(profile, ct);

        // Force the next write to fail: replace the storage directory with a plain file, so
        // PersistAsync's Directory.CreateDirectory(...) throws before anything is written.
        Directory.Delete(_dir, recursive: true);
        await File.WriteAllTextAsync(_dir, "blocker", ct);

        profile.Name = "Dev (renamed)";
        await Assert.ThrowsAnyAsync<IOException>(() => svc.SaveAsync(profile, ct));

        var all = await svc.GetAllAsync(ct);
        Assert.Equal("Dev", Assert.Single(all).Name);
    }
    [Fact]
    public async Task SignIn_reports_a_store_failure_instead_of_throwing()
    {
        var profiles = new Mock<IConnectionProfileService>();
        profiles.Setup(p => p.GetLastUsedAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("connections.json is unreadable"));
        var auth = new ProfileAuthService(profiles.Object);

        var result = await auth.SignInAsync(nint.Zero, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("connections.json is unreadable", result.Error);
    }

    [Fact]
    public async Task Unreadable_connections_file_is_moved_aside_and_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(StorePath, "{ not json", ct);
        var vm = new ConnectionManagerViewModel(
            new JsonConnectionProfileService(_dir), Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>());

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Empty(vm.Profiles);
        Assert.Contains("connections.json.corrupt", vm.SwitchError, StringComparison.Ordinal);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(StorePath + ".corrupt", ct));
    }

    [Fact]
    public async Task Locked_connections_read_as_empty_without_caching_and_writes_still_fail()
    {
        // WR-008: GetLastUsedAsync threw at startup (App.ShowMainWindow), which ended the app.
        var ct = TestContext.Current.CancellationToken;
        using (var seed = new JsonConnectionProfileService(_dir))
            await seed.SaveAsync(new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://org.crm.dynamics.com" }, ct);
        using var svc = new JsonConnectionProfileService(_dir); // fresh cache, like a new launch

        using (File.Open(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(await svc.GetAllAsync(ct));
            Assert.Contains("could not be opened", svc.LoadWarning, StringComparison.Ordinal);
            await Assert.ThrowsAnyAsync<IOException>(() => svc.SaveAsync(new ConnectionProfile { Name = "Other" }, ct));
        }

        Assert.Single(await svc.GetAllAsync(ct)); // the empty read was not cached
        Assert.False(File.Exists(StorePath + ".corrupt"));
    }

    [Fact]
    public async Task Locked_file_warning_clears_once_the_store_reads_again()
    {
        // WR-003: the locked-file warning stayed on the service and the Connections page after recovery.
        var ct = TestContext.Current.CancellationToken;
        using (var seed = new JsonConnectionProfileService(_dir))
            await seed.SaveAsync(new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://org.crm.dynamics.com" }, ct);
        using var svc = new JsonConnectionProfileService(_dir);
        var vm = new ConnectionManagerViewModel(svc, Mock.Of<IAuthService>(), Mock.Of<IDataverseConnectionService>());

        using (File.Open(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
            await vm.LoadCommand.ExecuteAsync(null);
        Assert.NotNull(vm.SwitchError);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Null(svc.LoadWarning);
        Assert.Null(vm.SwitchError);
        Assert.Single(vm.Profiles);
    }

    [Fact]
    public async Task SignIn_with_an_undecryptable_secret_asks_for_it_again()
    {
        // "AQID" is valid base64 but not DPAPI data: the same failure as a secret protected by
        // another Windows user or machine.
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(StorePath, $$"""
            {
              "LastUsedId": "{{id}}",
              "Profiles": [{
                "Id": "{{id}}", "Name": "App", "EnvironmentUrl": "https://org.crm.dynamics.com",
                "AuthType": "ClientSecret", "ClientId": "{{Guid.NewGuid()}}", "TenantId": "{{Guid.NewGuid()}}",
                "EncryptedClientSecret": "AQID"
              }]
            }
            """, ct);
        var auth = new ProfileAuthService(new JsonConnectionProfileService(_dir));

        var result = await auth.SignInAsync(nint.Zero, ct);

        Assert.False(result.Succeeded);
        Assert.StartsWith("Re-enter the client secret", result.Error, StringComparison.Ordinal);
    }
}

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

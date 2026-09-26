using SeedBomb.Services.History;
using SeedBomb.Services.Settings;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class JsonStoreRecoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "SeedBomb.Wpf.Tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Unreadable_history_is_moved_aside_before_the_next_write()
    {
        // WR-008: it used to read as empty, and the next run then overwrote every past run.
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "history.json");
        await File.WriteAllTextAsync(path, "{ not json", ct);
        using var svc = new JsonRunHistoryService(_dir);

        await svc.AddRunAsync(new RunRecord(Guid.NewGuid(), DateTimeOffset.Now, ["Account"], 1, TimeSpan.FromSeconds(1), true, 0), ct);

        Assert.Equal("{ not json", await File.ReadAllTextAsync(path + ".corrupt", ct));
        Assert.Single(await svc.GetRunsAsync(ct));
    }

    [Fact]
    public async Task Unreadable_settings_are_moved_aside_and_defaults_load()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "settings.json");
        await File.WriteAllTextAsync(path, "{ not json", ct);
        using var svc = new JsonSettingsService(_dir);

        Assert.Equal(AppSettings.Default, await svc.LoadAsync(ct));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path + ".corrupt", ct));
    }
}

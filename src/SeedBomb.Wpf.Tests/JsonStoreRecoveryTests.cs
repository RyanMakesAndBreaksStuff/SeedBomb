using SeedBomb.Services.History;
using SeedBomb.Services.Settings;
using SeedBomb.Core.Contracts;
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

        Assert.Empty(await svc.GetRunsAsync(ct));
        Assert.Contains("could not be read", svc.TakeLoadWarning(), StringComparison.Ordinal);

        await svc.AddRunAsync(new RunRecord(Guid.NewGuid(), DateTimeOffset.Now, ["Account"], 1, TimeSpan.FromSeconds(1), true, 0), ct);

        Assert.Equal("{ not json", await File.ReadAllTextAsync(path + ".corrupt", ct));
        Assert.Single(await svc.GetRunsAsync(ct));
        Assert.Null(svc.TakeLoadWarning());
    }

    [Fact]
    public async Task History_warning_survives_the_write_that_quarantined_the_file()
    {
        // WR-007: a finished run (AddRunAsync) quarantined the file and cleared the warning in one call.
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_dir, "history.json"), "{ not json", ct);
        using var svc = new JsonRunHistoryService(_dir);

        await svc.AddRunAsync(new RunRecord(Guid.NewGuid(), DateTimeOffset.Now, ["Account"], 1, TimeSpan.FromSeconds(1), true, 0), ct);

        Assert.Contains("could not be read", svc.TakeLoadWarning(), StringComparison.Ordinal);
        Assert.Null(svc.TakeLoadWarning()); // shown once
    }

    [Fact]
    public async Task Locked_settings_load_defaults_and_are_never_overwritten()
    {
        // WR-008: the IOException escaped LoadAsync, and App.OnStartup shut down with "Startup failed".
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "settings.json");
        using var svc = new JsonSettingsService(_dir);
        var mine = AppSettings.Default with { DarkTheme = !AppSettings.Default.DarkTheme };
        await svc.SaveAsync(mine, ct);

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(AppSettings.Default, await svc.LoadAsync(ct));
            Assert.Contains("could not be opened", svc.LoadWarning, StringComparison.Ordinal);
        }

        // Unlocked now, but this instance holds only defaults; saving them would erase the user's file.
        await Assert.ThrowsAsync<IOException>(() => svc.SaveAsync(AppSettings.Default, ct));
        Assert.False(File.Exists(path + ".corrupt"));
        Assert.Equal(mine, await svc.LoadAsync(ct));
        await svc.SaveAsync(mine, ct); // a successful load lifts the refusal
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
        Assert.Contains("could not be read", svc.LoadWarning, StringComparison.Ordinal);

        await svc.SaveAsync(AppSettings.Default, ct);
        Assert.Null(svc.LoadWarning);
    }

    [Fact]
    public async Task Out_of_range_settings_are_clamped_on_load()
    {
        // WR-014: a hand-edited batch size of 0 made Chunk throw mid-run; above 1000 every batch faulted.
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"),
            """{"defaultRecordCount":0,"defaultBatchSize":5000,"defaultDop":99}""", ct);
        using var svc = new JsonSettingsService(_dir);

        var loaded = await svc.LoadAsync(ct);

        Assert.Equal(1, loaded.DefaultRecordCount);
        Assert.Equal(GenerationLimits.MaxBatchSize, loaded.DefaultBatchSize);
        Assert.Equal(GenerationLimits.MaxDop, loaded.DefaultDop);
    }
}

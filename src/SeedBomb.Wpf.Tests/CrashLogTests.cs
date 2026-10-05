using SeedBomb;
using SeedBomb.Services.Diagnostics;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dg-crashlog", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Write_CreatesLogFileContainingExceptionText()
    {
        var path = CrashLog.Write(new InvalidOperationException("boom-marker"), _root);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Contains("boom-marker", File.ReadAllText(path!), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_AppendsEachCrashUnderATimestamp()
    {
        // IN-002: each crash overwrote the one log, losing the first (usually root-cause) crash.
        var path = CrashLog.Write(new InvalidOperationException("first-crash"), _root);
        CrashLog.Write(new InvalidOperationException("second-crash"), _root);

        var text = File.ReadAllText(path!);
        Assert.Contains("first-crash", text, StringComparison.Ordinal);
        Assert.Contains("second-crash", text, StringComparison.Ordinal);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T", text);
    }

    [Fact]
    public void Write_ReturnsNullAndDoesNotThrow_WhenDirectoryIsUnusable()
    {
        // A file where the directory should be — Directory.CreateDirectory throws IOException.
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "");

        Assert.Null(CrashLog.Write(new InvalidOperationException("x"), blocker));
    }

    [Fact]
    public void LogUnhandledException_WritesToCrashLog()
    {
        var path = Path.Combine(AppPaths.Root, CrashLog.FileName);

        App.LogUnhandledException(this, new UnhandledExceptionEventArgs(
            new InvalidOperationException("in-001-unhandled-marker"), isTerminating: true));

        Assert.True(File.Exists(path));
        Assert.Contains("in-001-unhandled-marker", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void LogUnobservedTaskException_WritesToCrashLogAndMarksObserved()
    {
        var path = Path.Combine(AppPaths.Root, CrashLog.FileName);
        var args = new UnobservedTaskExceptionEventArgs(
            new AggregateException(new InvalidOperationException("in-001-unobserved-marker")));

        App.LogUnobservedTaskException(null, args);

        Assert.True(args.Observed);
        Assert.Contains("in-001-unobserved-marker", File.ReadAllText(path), StringComparison.Ordinal);
    }
}

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
    public void Write_ReturnsNullAndDoesNotThrow_WhenDirectoryIsUnusable()
    {
        // A file where the directory should be — Directory.CreateDirectory throws IOException.
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "");

        Assert.Null(CrashLog.Write(new InvalidOperationException("x"), blocker));
    }
}

using System.IO;

namespace Seedbomb.Services.Diagnostics;

/// <summary>Best-effort crash diagnostics. Never throws — a failed log must not mask the crash.</summary>
public static class CrashLog
{
    /// <summary>File name written inside the log directory.</summary>
    public const string FileName = "startup-error.log";

    /// <summary>
    /// Writes <paramref name="ex"/> to the crash log and returns the path written, or
    /// <see langword="null"/> when the write failed.
    /// </summary>
    /// <param name="ex">Exception to record.</param>
    /// <param name="directoryOverride">Test seam. Null uses %LOCALAPPDATA%\SeedBomb.</param>
    public static string? Write(Exception ex, string? directoryOverride = null)
    {
        try
        {
            var directory = directoryOverride ?? AppPaths.Root;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            File.WriteAllText(path, ex?.ToString() ?? string.Empty);
            return path;
        }
        catch
        {
            return null;
        }
    }
}

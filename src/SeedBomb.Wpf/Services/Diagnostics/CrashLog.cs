using System.IO;

namespace SeedBomb.Services.Diagnostics;

/// <summary>Best-effort crash diagnostics. Never throws — a failed log must not mask the crash.</summary>
public static class CrashLog
{
    /// <summary>File name written inside the log directory.</summary>
    public const string FileName = "startup-error.log";

    /// <summary>
    /// Appends <paramref name="ex"/> under a timestamp to the crash log and returns the path
    /// written, or <see langword="null"/> when the write failed.
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
            // IN-002: append, so the first (usually root-cause) crash survives the ones it triggers.
            // ponytail: unbounded; crashes are rare. Roll the file if it ever grows.
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{ex?.ToString() ?? string.Empty}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return null;
        }
    }
}
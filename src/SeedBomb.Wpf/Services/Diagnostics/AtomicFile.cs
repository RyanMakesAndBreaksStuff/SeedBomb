using System.IO;
using System.Text;

namespace SeedBomb.Services.Diagnostics;

/// <summary>
/// Single owner of how the JSON stores replace their files: write a sibling temp file, flush it,
/// then swap it in. A crash or full disk mid-write leaves the previous file whole, never truncated.
/// </summary>
public static class AtomicFile
{
    /// <summary>Replaces <paramref name="path"/> with <paramref name="contents"/> (UTF-8) in one swap.</summary>
    /// <param name="path">Destination file. Its directory must already exist.</param>
    /// <param name="contents">The complete new file text.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        var tmp = path + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(contents), ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true); // on disk before the swap, so a power cut can't leave an empty file
        }

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }

    /// <summary>
    /// Moves an unreadable store file aside as <c>&lt;file&gt;.corrupt</c> (replacing an older one) so
    /// the store can start empty without its next write destroying the data.
    /// </summary>
    /// <param name="path">The unreadable file.</param>
    /// <returns>Where the file now lives.</returns>
    public static string Quarantine(string path)
    {
        var corrupt = path + ".corrupt";
        File.Move(path, corrupt, overwrite: true);
        return corrupt;
    }
}

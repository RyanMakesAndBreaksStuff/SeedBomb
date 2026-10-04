using System.IO;

namespace SeedBomb.Services.Diagnostics;

/// <summary>Single owner of the per-user data root. Carries the pre-rename DataGen folder across once.</summary>
public static class AppPaths
{
    private static readonly Lazy<(string Root, string? Warning)> Resolved =
        new(() => Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary><c>%LOCALAPPDATA%\SeedBomb</c>, or the legacy DataGen folder while it can't be moved. Created on first access.</summary>
    public static string Root => Resolved.Value.Root;

    /// <summary>WR-008: why <see cref="Root"/> is still the legacy folder. Null when there is nothing to report.</summary>
    public static string? MigrationWarning => Resolved.Value.Warning;

    /// <summary><c>%LOCALAPPDATA%\SeedBomb\logs</c>. Created on first access.</summary>
    public static string Logs => Directory.CreateDirectory(Path.Combine(Root, "logs")).FullName;

    /// <summary>Test seam: resolves the data root under <paramref name="local"/>.</summary>
    internal static (string Root, string? Warning) Resolve(string local)
    {
        var root = Path.Combine(local, "SeedBomb");
        var legacy = Path.Combine(local, "DataGen");

        // One move carries profiles, settings, connections and the MSAL token cache across the
        // DataGen → SeedBomb rename. If it fails, keep using the legacy folder so nothing looks lost;
        // the new root is not created, so the move is retried next launch.
        if (!Directory.Exists(root) && Directory.Exists(legacy))
        {
            try
            {
                Directory.Move(legacy, root);
            }
            catch (Exception ex) when (AtomicFile.IsUnavailable(ex))
            {
                return (legacy, $"SeedBomb could not move its data from {legacy} to {root} ({ex.Message}), so it is still using {legacy}. Close anything using that folder and restart to finish the move.");
            }
        }

        return (Directory.CreateDirectory(root).FullName, null);
    }
}

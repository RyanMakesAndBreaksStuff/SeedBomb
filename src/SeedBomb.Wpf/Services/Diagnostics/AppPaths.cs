using System.IO;

namespace Seedbomb.Services.Diagnostics;

/// <summary>Single owner of the per-user data root. Carries the pre-rename DataGen folder across once.</summary>
public static class AppPaths
{
    private static readonly Lazy<string> RootPath =
        new(Resolve, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary><c>%LOCALAPPDATA%\SeedBomb</c>. Created on first access.</summary>
    public static string Root => RootPath.Value;

    /// <summary><c>%LOCALAPPDATA%\SeedBomb\logs</c>. Created on first access.</summary>
    public static string Logs => Directory.CreateDirectory(Path.Combine(Root, "logs")).FullName;

    private static string Resolve()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "SeedBomb");
        var legacy = Path.Combine(local, "DataGen");

        // One move carries profiles, settings, connections and the MSAL token cache across the
        // DataGen → SeedBomb rename. A failed move must not block startup — a fresh root is used.
        if (!Directory.Exists(root) && Directory.Exists(legacy))
        {
            try { Directory.Move(legacy, root); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Directory.CreateDirectory(root).FullName;
    }
}

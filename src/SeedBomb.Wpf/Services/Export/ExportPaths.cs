using System.IO;

namespace SeedBomb.Services.Export;

/// <summary>Resolves the destination folder for user-facing CSV exports.</summary>
public static class ExportPaths
{
    /// <summary>Returns the current user's Downloads folder. Does not create it.</summary>
    public static string Downloads() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");
}
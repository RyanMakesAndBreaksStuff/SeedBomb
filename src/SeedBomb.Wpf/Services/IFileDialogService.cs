namespace SeedBomb.Services;

/// <summary>WR-001: profile file pickers, kept out of view models and page code-behind.</summary>
public interface IFileDialogService
{
    /// <summary>Asks for a profile file to import.</summary>
    /// <returns>The chosen path, or null when cancelled.</returns>
    string? PickProfileToImport();

    /// <summary>Asks where to export the profile <paramref name="name"/>.</summary>
    /// <param name="name">Profile name, used as the suggested file name.</param>
    /// <returns>The chosen path, or null when cancelled.</returns>
    string? PickProfileExportPath(string name);
}

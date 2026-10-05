using Microsoft.Win32;

namespace SeedBomb.Services;

/// <summary>Win32 common-dialog implementation of <see cref="IFileDialogService"/>.</summary>
public sealed class FileDialogService : IFileDialogService
{
    /// <inheritdoc />
    public string? PickProfileToImport()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Profile (*.profile.json)|*.profile.json|JSON (*.json)|*.json|All files|*.*",
            Title = "Import profile",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <inheritdoc />
    public string? PickProfileExportPath(string name)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Profile (*.profile.json)|*.profile.json",
            FileName = $"{name}.profile.json",
            Title = "Export profile",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}

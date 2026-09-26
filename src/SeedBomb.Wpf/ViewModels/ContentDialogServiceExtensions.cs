using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace SeedBomb.ViewModels;

/// <summary>Single owner of the app's themed yes/no confirmation.</summary>
public static class ContentDialogServiceExtensions
{
    /// <summary>
    /// Shows a themed confirmation and returns <see langword="true"/> only when the user picks
    /// <paramref name="primary"/>.
    /// </summary>
    /// <param name="dialogs">The app's dialog host.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="content">The question shown to the user.</param>
    /// <param name="primary">Label of the confirming button, e.g. "Delete".</param>
    /// <param name="close">Label of the dismissing button.</param>
    public static async Task<bool> ConfirmAsync(
        this IContentDialogService dialogs, string title, string content, string primary, string close = "Cancel")
    {
        var result = await dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
        });
        return result == ContentDialogResult.Primary;
    }
}

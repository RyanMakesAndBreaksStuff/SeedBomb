using SeedBomb.Services.Profiles;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace SeedBomb.ViewModels;

/// <summary>Single owner of the app's themed yes/no confirmation and profile-name prompt.</summary>
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

    /// <summary>
    /// Asks for a profile name. Save stays disabled until the name is lower-kebab, so the profile
    /// name always equals its file name. Returns <see langword="null"/> on cancel.
    /// </summary>
    /// <param name="dialogs">The app's dialog host.</param>
    /// <param name="suggested">Initial text in the name box.</param>
    public static async Task<string?> AskProfileNameAsync(this IContentDialogService dialogs, string suggested)
    {
        var box = new System.Windows.Controls.TextBox { Text = suggested };
        var hint = new System.Windows.Controls.TextBlock
        {
            Text = "Lowercase letters, numbers and dashes only, e.g. contact-acct",
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
        };
        hint.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "DG.Tertiary");
        var dialog = new ContentDialog
        {
            Title = "Profile name",
            Content = new System.Windows.Controls.StackPanel { Children = { box, hint } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = JsonProfileService.IsValidName(suggested),
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = JsonProfileService.IsValidName(box.Text);

        return await dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? box.Text : null;
    }
}

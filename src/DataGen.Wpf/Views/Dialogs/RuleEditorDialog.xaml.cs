using Seedbomb.ViewModels;
using Wpf.Ui.Controls;

namespace Seedbomb.Views.Dialogs;

/// <summary>
/// Rule editor dialog content, hosted via the app's <see cref="Wpf.Ui.IContentDialogService"/>
/// (<c>await contentDialogService.ShowAsync(dialog, cancellationToken)</c>).
/// </summary>
public partial class RuleEditorDialog : ContentDialog
{
    /// <summary>Initialises the dialog.</summary>
    /// <param name="viewModel">The bound view-model.</param>
    /// <param name="isEditMode">True to show the "Remove rule" footer action (editing an existing rule).</param>
    public RuleEditorDialog(RuleEditorViewModel viewModel, bool isEditMode = false)
    {
        DataContext = viewModel;
        InitializeComponent();

        SecondaryButtonText = isEditMode ? "Remove rule" : string.Empty;
        IsSecondaryButtonEnabled = isEditMode;
    }
}

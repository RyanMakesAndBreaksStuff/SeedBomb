using System.Windows.Controls;
using Seedbomb.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.Views.Pages;

/// <summary>
/// Rules editor as a full page (1a/3b). Previously a dialog owned by the Generate flow.
/// Navigation parameter carries the profile + table to edit.
/// </summary>
public partial class RulesPage : Page, INavigableView<RuleEditorViewModel>
{
    /// <inheritdoc />
    public RuleEditorViewModel ViewModel { get; }

    /// <summary>Initialises the page.</summary>
    public RulesPage(RuleEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }
}

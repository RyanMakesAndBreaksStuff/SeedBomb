using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DataGen.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.ViewModels;
using Seedbomb.ViewModels.Controls;
using Seedbomb.Views.Dialogs;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Seedbomb.Views.Controls;

/// <summary>One vertical tab in the table list — table plus its active-rule-count badge.</summary>
public sealed record TableTabEntry(string LogicalName, string DisplayName, int RuleCount);

/// <summary>
/// Field Rules board (Mock F1/F1b): table tabs with rule-count badges, active-rule rows for the
/// selected table, and an Add-rule action that opens <see cref="RuleEditorDialog"/> via the app's
/// existing <see cref="IContentDialogService"/>. Owns presentation only — rule state lives entirely
/// in the attached <see cref="FieldRulesViewModel"/> (Task 7); metadata is supplied by the page's
/// <see cref="Seedbomb.ViewModels.GenerateViewModel"/> via the <see cref="Metadata"/> binding.
/// </summary>
public partial class FieldRulesControl : UserControl
{
    private readonly FieldRulesViewModel _vm = new();

    /// <summary>Gets the underlying board ViewModel for parent attachment.</summary>
    public FieldRulesViewModel ViewModel => _vm;

    /// <summary>Table tabs with rule-count badges, derived from <see cref="Tables"/> plus the board draft.</summary>
    public ObservableCollection<TableTabEntry> TableTabs { get; } = [];

    /// <summary>Identifies the <see cref="Tables"/> dependency property.</summary>
    public static readonly DependencyProperty TablesProperty = DependencyProperty.Register(
        nameof(Tables), typeof(IReadOnlyList<EntitySummary>), typeof(FieldRulesControl),
        new PropertyMetadata(Array.Empty<EntitySummary>(), OnBoardInputsChanged));

    /// <summary>Selected entities for this run — supplies the table tab list.</summary>
    public IReadOnlyList<EntitySummary> Tables
    {
        get => (IReadOnlyList<EntitySummary>)GetValue(TablesProperty);
        set => SetValue(TablesProperty, value);
    }

    /// <summary>Identifies the <see cref="Metadata"/> dependency property.</summary>
    public static readonly DependencyProperty MetadataProperty = DependencyProperty.Register(
        nameof(Metadata), typeof(IReadOnlyDictionary<string, EntityMetadata>), typeof(FieldRulesControl),
        new PropertyMetadata(null));

    /// <summary>Full live entity metadata keyed by logical name, loaded by the page's ViewModel.</summary>
    public IReadOnlyDictionary<string, EntityMetadata>? Metadata
    {
        get => (IReadOnlyDictionary<string, EntityMetadata>?)GetValue(MetadataProperty);
        set => SetValue(MetadataProperty, value);
    }

    /// <summary>Identifies the <see cref="Seed"/> dependency property.</summary>
    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(int), typeof(FieldRulesControl), new PropertyMetadata(42));

    /// <summary>Generation seed — feeds the editor's deterministic preview substream.</summary>
    public int Seed
    {
        get => (int)GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    /// <summary>Identifies the <see cref="RunId"/> dependency property.</summary>
    public static readonly DependencyProperty RunIdProperty = DependencyProperty.Register(
        nameof(RunId), typeof(string), typeof(FieldRulesControl), new PropertyMetadata(""));

    /// <summary>This run's id — feeds the editor's <c>{runId}</c> pattern-token preview.</summary>
    public string RunId
    {
        get => (string)GetValue(RunIdProperty);
        set => SetValue(RunIdProperty, value);
    }

    /// <summary>Identifies the <see cref="DefaultRecordCount"/> dependency property.</summary>
    public static readonly DependencyProperty DefaultRecordCountProperty = DependencyProperty.Register(
        nameof(DefaultRecordCount), typeof(int), typeof(FieldRulesControl), new PropertyMetadata(10));

    // ponytail: editor preview uses the wizard-level default record count, not each table's live
    // configured count (Configure and Rules cards are both visible at once, so the accurate count
    // can change after the editor opens). Review preflight re-validates every rule against the
    // real per-table count before Start unlocks, so this is a preview-only approximation with no
    // effect on what is ever generated. Upgrade path: bind a live per-table count if the imprecise
    // preview proves confusing.
    /// <summary>Record count used for editor preview/validation (sequence overflow, pattern width).</summary>
    public int DefaultRecordCount
    {
        get => (int)GetValue(DefaultRecordCountProperty);
        set => SetValue(DefaultRecordCountProperty, value);
    }

    /// <summary>Initialises the control.</summary>
    public FieldRulesControl()
    {
        InitializeComponent();
        // Set VM on content root only — not on the UserControl itself.
        // Parent DP bindings (Tables/Seed/RunId/…) must keep resolving against GenerateViewModel.
        if (Content is FrameworkElement root)
            root.DataContext = _vm;
        _vm.DraftChanged += (_, _) => RefreshTabs();
    }

    private static void OnBoardInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FieldRulesControl)d).RefreshTabs();

    private void RefreshTabs()
    {
        var counts = _vm.GetRules();
        var previouslySelected = _vm.SelectedTable;

        TableTabs.Clear();
        foreach (var entity in Tables)
        {
            var count = counts.TryGetValue(entity.LogicalName, out var cols) ? cols.Count : 0;
            TableTabs.Add(new TableTabEntry(entity.LogicalName, entity.DisplayName, count));
        }

        if (TableTabs.Count > 0 && !TableTabs.Any(t => t.LogicalName == previouslySelected))
            _vm.SelectTable(TableTabs[0].LogicalName);
    }

    private void OnTableTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TableTabsListBox.SelectedItem is TableTabEntry entry)
            _vm.SelectTable(entry.LogicalName);
    }

    private async void OnAddRuleClick(object sender, RoutedEventArgs e) => await OpenEditorAsync(null);

    private async void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleRow row })
            await OpenEditorAsync(row);
    }

    private async Task OpenEditorAsync(RuleRow? existing)
    {
        var table = _vm.SelectedTable;
        if (string.IsNullOrEmpty(table) || Metadata is null || !Metadata.TryGetValue(table, out var meta))
            return;

        var editorVm = new RuleEditorViewModel(meta, DefaultRecordCount, Seed, RunId);
        if (existing is not null)
        {
            editorVm.SelectedColumn = editorVm.SettableColumns.FirstOrDefault(c => c.LogicalName == existing.Column);
            // Restore op + params — otherwise edit always lands on the type-default op (constant).
            editorVm.ApplyExistingRule(existing.Rule);
        }
        else if (editorVm.SettableColumns.Count > 0)
        {
            // Pre-select first settable column so default op chips (incl. null/platform default) are immediately usable.
            editorVm.SelectedColumn = editorVm.SettableColumns[0];
        }

        var dialogService = ((App)Application.Current).Services.GetRequiredService<IContentDialogService>();
        var dialog = new RuleEditorDialog(editorVm, isEditMode: existing is not null);
        var result = await dialogService.ShowAsync(dialog, CancellationToken.None);

        if (result == ContentDialogResult.Secondary && existing is not null)
        {
            _vm.RemoveRule(table, existing.Column);
            return;
        }

        if (result != ContentDialogResult.Primary)
            return;

        var column = editorVm.SelectedColumn;
        var rule = editorVm.BuildRule();
        if (column is null || rule is null)
            return;

        var preview = editorVm.PreviewValues.Count > 0 ? editorVm.PreviewValues[0] : string.Empty;
        _vm.SetRule(table, column.LogicalName, rule, column.DisplayName, preview);
    }
}

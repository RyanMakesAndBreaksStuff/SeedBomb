using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Seedbomb.Services.Dataverse;
using Wpf.Ui;

namespace Seedbomb.ViewModels;

/// <summary>One row in the column picker — settable or platform-owned-with-reason (§3.2).</summary>
/// <param name="LogicalName">Attribute logical name.</param>
/// <param name="DisplayName">User-facing label.</param>
/// <param name="TypeLabel">Friendly type name shown in the picker row.</param>
/// <param name="IsSelectable">True = eligible rule target.</param>
/// <param name="DisabledReason">Copy shown when <paramref name="IsSelectable"/> is false; null when selectable.</param>
public sealed record PickerColumn(string LogicalName, string DisplayName, string TypeLabel,
    bool IsSelectable, string? DisabledReason)
{
    /// <summary>Handoff alias for <see cref="DisplayName"/>.</summary>
    public string Name => DisplayName;

    /// <summary>Handoff alias for <see cref="TypeLabel"/>.</summary>
    public string TypeDetail => TypeLabel;

    /// <summary>Mapped / Unmapped / Required / Disabled. XAML maps to DG.* — no Brush.</summary>
    public string StateKey { get; init; } = "Unmapped";

    /// <summary>CollectionView group: Mapped, Unmapped · required, or Unmapped.</summary>
    public string GroupName { get; init; } = "Unmapped";

    /// <summary>Group display rank — Mapped, Required, Unmapped, Disabled, in that order regardless of metadata order.</summary>
    public int GroupOrder { get; init; }
}

/// <summary>One selectable operation chip in the rule editor (Mock F2 op cards).</summary>
/// <param name="Op">Wire op id (<c>constant</c>, <c>oneOf</c>, …).</param>
/// <param name="Title">Short label shown on the card.</param>
/// <param name="Hint">One-line description under the title.</param>
public sealed record OpOption(string Op, string Title, string Hint);

/// <summary>Chip for Mapped / Required / All column filters.</summary>
/// <param name="Key">Mapped, Required, or All.</param>
/// <param name="Label">Chip label including count.</param>
/// <param name="Count">Columns in this chip.</param>
public sealed record ColumnFilterMode(string Key, string Label, int Count);

/// <summary>One table in the Rules header switcher.</summary>
/// <param name="LogicalName">Table logical name.</param>
/// <param name="DisplayName">Label shown in the combo (logical name until metadata loads).</param>
public sealed record RuleTableOption(string LogicalName, string DisplayName);

/// <summary>One preview sample row. <see cref="ValueKind"/> is Blank or Value — no brush.</summary>
/// <param name="DisplayValue">Rendered sample, or <c>— blank —</c>.</param>
/// <param name="ValueKind">Blank or Value. XAML maps to DG.* — no Brush.</param>
public sealed record PreviewRow(string DisplayValue, string ValueKind);

/// <summary>Insertable pattern token chip.</summary>
/// <param name="Name">Token text appended to the template, e.g. <c>{seq}</c>.</param>
public sealed record TokenChip(string Name);

/// <summary>One checkable option for a Choice/Two-Options <c>oneOf</c> rule.</summary>
public sealed partial class OptionChoice : ObservableObject
{
    /// <summary>The option set value.</summary>
    public int Value { get; }

    /// <summary>The option's display label.</summary>
    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Initialises the option.</summary>
    public OptionChoice(int value, string label)
    {
        Value = value;
        Label = label;
    }
}

/// <summary>Cross-cutting optionals shared by the rule editor and its collaborators.</summary>
/// <param name="Dialogs">Content dialogs for delete confirmation; null in tests.</param>
/// <param name="Snackbar">Failure toasts; null in tests.</param>
/// <param name="Logger">Rules-page logger; null in tests.</param>
/// <param name="Picker">Lookup record picker; null when lookup pick is unavailable.</param>
public sealed record RuleEditorServices(
    IContentDialogService? Dialogs = null,
    ISnackbarService? Snackbar = null,
    ILogger<RuleEditorViewModel>? Logger = null,
    ILookupRecordPicker? Picker = null);

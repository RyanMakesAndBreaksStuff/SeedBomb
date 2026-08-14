using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Profiles;
using Seedbomb.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace Seedbomb.ViewModels;

/// <summary>One row in the Profiles list.</summary>
/// <param name="Name">Profile display name.</param>
/// <param name="VersionLabel">e.g. v1.</param>
/// <param name="SummaryLine">e.g. 3 tables · 11 rules · 9,500 rows.</param>
/// <param name="Description">Optional profile description.</param>
/// <param name="Seed">Pinned seed, if any.</param>
/// <param name="RuleCount">Active column rules.</param>
/// <param name="UnmappedRequiredHint">Footer hint when unmapped required columns exist. Empty until metadata is wired.</param>
public sealed record ProfileListItem(
    string Name,
    string VersionLabel,
    string SummaryLine,
    string? Description = null,
    int? Seed = null,
    int RuleCount = 0,
    string UnmappedRequiredHint = "");

/// <summary>One ruled column in the selected profile's detail table.</summary>
/// <param name="Table">Table logical name.</param>
/// <param name="Column">Column logical name.</param>
/// <param name="OperationSummary">Rule op discriminator or type name.</param>
public sealed record ProfileRuleRow(string Table, string Column, string OperationSummary);

/// <summary>
/// Profiles manager + visual-only import (Mock F5). Schema stage via
/// <see cref="IProfileService"/>; metadata stage via <see cref="ProfileImport"/>.
/// Never renders profile JSON — only list rows and InfoBar summaries.
/// </summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileService _profiles;
    private readonly RulesNavigationRequest? _rulesRequest;
    private readonly IAppNavigator? _navigator;
    private readonly IContentDialogService? _dialogs;
    private readonly Dictionary<string, Profile> _profilesByName = new(StringComparer.OrdinalIgnoreCase);

    private List<ProfileListItem> _allItems = [];
    private int _sortMode;
    private CancellationTokenSource? _loadCts;

    /// <summary>Initialises the view-model.</summary>
    public ProfilesViewModel(
        IProfileService profiles,
        RulesNavigationRequest? rulesRequest = null,
        IAppNavigator? navigator = null,
        IContentDialogService? dialogs = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _rulesRequest = rulesRequest;
        _navigator = navigator;
        _dialogs = dialogs;
    }

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        try
        {
            await RefreshAsync(_loadCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public override Task OnNavigatedFromAsync()
    {
        _loadCts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Saved profiles (excludes autosave draft).</summary>
    public ObservableCollection<ProfileListItem> Items { get; } = [];

    /// <summary>In-memory filtered view of <see cref="Items"/>. Does not reload from disk.</summary>
    public IEnumerable<ProfileListItem> VisibleItems =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Items
            : Items.Where(p => p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    /// <summary>Footer count of the visible set.</summary>
    public string CountLabel
    {
        get
        {
            var count = 0;
            foreach (var _ in VisibleItems)
                count++;
            return count == 1 ? "1 profile" : $"{count} profiles";
        }
    }

    /// <summary>Alias for <see cref="ImportFromFileCommand"/> (page header binding).</summary>
    public IRelayCommand ImportCommand => ImportFromFileCommand;

    /// <summary>Flattened rules of the selected profile.</summary>
    public ObservableCollection<ProfileRuleRow> SelectedProfileRules { get; } = [];

    /// <summary>Footer rule count plus optional unmapped hint.</summary>
    public string SelectedProfileRuleSummary
    {
        get
        {
            if (SelectedItem is null) return "";
            var unmapped = string.IsNullOrEmpty(SelectedItem.UnmappedRequiredHint)
                ? ""
                : " · " + SelectedItem.UnmappedRequiredHint;
            return $"{SelectedItem.RuleCount} rules{unmapped}";
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditRulesCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedProfileRuleSummary))]
    private ProfileListItem? _selectedItem;

    [ObservableProperty]
    private string _newProfileName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    /// <summary>Whether the status InfoBar should show.</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _selectedDetailTab;

    [ObservableProperty]
    private string _selectedProfileDescription = "";

    [ObservableProperty]
    private string _selectedProfileSeed = "";

    [ObservableProperty]
    private string _selectedProfileEditedLabel = "";

    [ObservableProperty]
    private string _tablesAndVolumeSummary = "";

    [ObservableProperty]
    private string _profileRunHistory = "";

    // ── Import summary pane (Mock F5 right) ───────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManager))]
    [NotifyCanExecuteChangedFor(nameof(OpenInBoardCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditRulesCommand))]
    private bool _showImportSummary;

    /// <summary>Manager list visible when not showing an import summary.</summary>
    public bool ShowManager => !ShowImportSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportApplied))]
    private string? _importAppliedMessage;

    /// <summary>Whether the applied InfoBar should show.</summary>
    public bool HasImportApplied => !string.IsNullOrEmpty(ImportAppliedMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportAdjusted))]
    private string? _importAdjustedMessage;

    /// <summary>Whether the adjusted InfoBar should show.</summary>
    public bool HasImportAdjusted => !string.IsNullOrEmpty(ImportAdjustedMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportNotImported))]
    private string? _importNotImportedMessage;

    /// <summary>Whether the not-imported InfoBar should show.</summary>
    public bool HasImportNotImported => !string.IsNullOrEmpty(ImportNotImportedMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportInfo))]
    private string? _importInfoMessage;

    /// <summary>Whether the info InfoBar should show.</summary>
    public bool HasImportInfo => !string.IsNullOrEmpty(ImportInfoMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSchemaError))]
    private string? _schemaErrorMessage;

    /// <summary>Whether the schema-error InfoBar should show.</summary>
    public bool HasSchemaError => !string.IsNullOrEmpty(SchemaErrorMessage);

    /// <summary>Pending import waiting for Open in board / Discard.</summary>
    public ProfileImportReport? PendingImport { get; private set; }

    /// <summary>True when the caller applied the pending import to the board.</summary>
    public bool AppliedToBoard { get; private set; }

    // ── Host callbacks (wired by GenerateViewModel before ShowAsync) ──────────

    /// <summary>Snapshots the current wizard state as a profile (Save current).</summary>
    public Func<string, Profile>? CaptureCurrent { get; set; }

    /// <summary>True when the field-rules draft is dirty (Load confirm).</summary>
    public Func<bool>? IsBoardDirty { get; set; }

    /// <summary>Live entity metadata for import/load validation (same path as Task 9).</summary>
    public Func<IReadOnlyDictionary<string, EntityMetadata>>? GetMetadata { get; set; }

    /// <summary>Current run id for pattern worst-case length during import validation.</summary>
    public Func<string>? GetRunId { get; set; }

    /// <summary>Optional confirm: return true to proceed overwriting a dirty board.</summary>
    public Func<string, bool>? ConfirmOverwrite { get; set; }

    /// <summary>File picker: open path for import, or null if cancelled.</summary>
    public Func<string?>? PickImportPath { get; set; }

    /// <summary>File picker: export destination, or null if cancelled.</summary>
    public Func<string, string?>? PickExportPath { get; set; }

    /// <summary>Prompt for a new profile name (Save / Duplicate); null = cancel.</summary>
    public Func<string, string?>? PromptName { get; set; }

    /// <summary>Confirm delete; true = delete.</summary>
    public Func<string, bool>? ConfirmDelete { get; set; }

    /// <summary>
    /// Raised when the host dialog should close and return to the Field Rules board
    /// (Open in board applied a pending import).
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the page should open Rules for the named profile.</summary>
    public event EventHandler<string>? EditRulesRequested;

    /// <summary>Reloads the profile list from the store.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var names = await _profiles.ListAsync(ct);
        var projected = new List<ProfileListItem>();
        _profilesByName.Clear();
        foreach (var name in names)
        {
            var profile = await _profiles.LoadAsync(name, ct);
            _profilesByName[profile.Name] = profile;
            projected.Add(Project(profile));
        }

        IEnumerable<ProfileListItem> ordered = _sortMode switch
        {
            1 => projected.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase), // placeholder: no mtime
            2 => projected.OrderByDescending(p => ParseRowCount(p.SummaryLine)),
            _ => projected.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
        };

        var selectedName = SelectedItem?.Name;
        _allItems = ordered.ToList();
        Items.Clear();
        foreach (var item in _allItems)
            Items.Add(item);

        ApplySearchFilter();
        SelectedItem = null;
        SelectedItem = Items.FirstOrDefault(i =>
            string.Equals(i.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(CountLabel));
        RebuildSelectedDetail();
    }

    /// <summary>Loads the selected (or parameter) profile into the pending-import slot (then Open in board).</summary>
    [RelayCommand]
    private async Task LoadAsync(ProfileListItem? item)
    {
        if (item is not null)
            SelectedItem = item;
        if (SelectedItem is null) return;

        if (IsBoardDirty?.Invoke() == true)
        {
            var ok = ConfirmOverwrite?.Invoke(
                "The rules board has unsaved changes. Load this profile and overwrite the draft?") ?? true;
            if (!ok) return;
        }

        try
        {
            var profile = await _profiles.LoadAsync(SelectedItem.Name);
            if (GetMetadata is null)
            {
                SetStatus($"Loaded “{SelectedItem.Name}”.");
                return;
            }

            PresentImport(profile, sourceLabel: SelectedItem.Name);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>Exports the selected profile to a user-chosen path.</summary>
    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    private async Task ExportAsync()
    {
        if (SelectedItem is null) return;
        var dest = PickExportPath?.Invoke(SelectedItem.Name);
        if (string.IsNullOrWhiteSpace(dest)) return;

        try
        {
            await _profiles.ExportAsync(SelectedItem.Name, dest);
            SetStatus($"Exported “{SelectedItem.Name}”.");
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>Duplicates the selected profile under a new name.</summary>
    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    private async Task DuplicateAsync()
    {
        if (SelectedItem is null) return;

        string? newName;
        var suggested = $"{SelectedItem.Name} copy";
        if (PromptName is not null)
            newName = PromptName(suggested) ?? suggested;
        else if (_dialogs is not null)
        {
            newName = await AskNameAsync(suggested);
            if (string.IsNullOrWhiteSpace(newName)) return;
        }
        else
            newName = suggested;

        if (string.IsNullOrWhiteSpace(newName)) return;

        try
        {
            await _profiles.DuplicateAsync(SelectedItem.Name, newName.Trim());
            await RefreshAsync();
            SelectedItem = Items.FirstOrDefault(i =>
                string.Equals(i.Name, newName.Trim(), StringComparison.OrdinalIgnoreCase));
            SetStatus($"Duplicated as “{newName.Trim()}”.");
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>Deletes the selected profile after confirm.</summary>
    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    private async Task DeleteAsync()
    {
        if (SelectedItem is null) return;
        var ok = await ConfirmDeleteAsync(SelectedItem.Name);
        if (!ok) return;

        try
        {
            await _profiles.DeleteAsync(SelectedItem.Name);
            await RefreshAsync();
            SelectedItem = null;
            SetStatus("Profile deleted.");
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>Snapshots the current board as a new named profile.</summary>
    [RelayCommand]
    private async Task SaveCurrentAsNewAsync()
    {
        if (CaptureCurrent is null)
        {
            SetError("No current board is available to save.");
            return;
        }

        var name = !string.IsNullOrWhiteSpace(NewProfileName)
            ? NewProfileName.Trim()
            : await AskNameAsync("new-profile");
        if (string.IsNullOrWhiteSpace(name))
        {
            SetError("Enter a name for the new profile.");
            return;
        }

        try
        {
            var profile = CaptureCurrent(name.Trim());
            await _profiles.SaveAsync(profile);
            NewProfileName = "";
            await RefreshAsync();
            SelectedItem = Items.FirstOrDefault(i =>
                string.Equals(i.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            SetStatus($"Saved “{profile.Name}”.");
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Import from file: schema stage (Task 10) then metadata stage → visual summary only.
    /// </summary>
    [RelayCommand]
    private async Task ImportFromFileAsync()
    {
        var path = PickImportPath?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) return;

        await ImportFromPathAsync(path);
    }

    /// <summary>
    /// Test/UI entry: run schema import then metadata validation for <paramref name="sourcePath"/>.
    /// </summary>
    public async Task ImportFromPathAsync(string sourcePath, CancellationToken ct = default)
    {
        SchemaErrorMessage = null;
        StatusMessage = null;
        HasError = false;

        var (profile, error) = await _profiles.ImportAsync(sourcePath, ct);
        if (profile is null)
        {
            SchemaErrorMessage = error ?? "not a valid profile";
            ShowImportSummary = true;
            ImportAppliedMessage = null;
            ImportAdjustedMessage = null;
            ImportNotImportedMessage = null;
            ImportInfoMessage = null;
            PendingImport = null;
            return;
        }

        await RefreshAsync(ct);

        if (GetMetadata is null)
        {
            SetStatus($"Imported “{profile.Name}”.");
            return;
        }

        PresentImport(profile, sourceLabel: Path.GetFileName(sourcePath));
    }

    /// <summary>
    /// Test entry: metadata-validate an already-parsed profile (after <see cref="IProfileService.ImportAsync"/>).
    /// </summary>
    public ProfileImportReport PresentImport(Profile profile, string sourceLabel)
    {
        var metadata = GetMetadata?.Invoke()
            ?? throw new InvalidOperationException("Metadata provider not wired for profile import.");
        var runId = GetRunId?.Invoke() ?? "";

        var report = ProfileImport.ValidateAgainstMetadata(profile, metadata, runId);
        PendingImport = report;
        AppliedToBoard = false;
        SchemaErrorMessage = null;
        ShowImportSummary = true;

        ImportAppliedMessage = report.AppliedRuleCount > 0 || report.AppliedTableSummaries.Count > 0
            ? $"Applied — {report.AppliedRuleCount} rule(s) across {report.AppliedTableSummaries.Count} table(s). "
              + string.Join(", ", report.AppliedTableSummaries)
              + ". Record counts adopted from the profile."
            : null;

        ImportAdjustedMessage = report.Adjusted.Count > 0
            ? "Adjusted — " + report.Adjusted.Count + " rule(s) clamped.\n"
              + string.Join("\n", report.Adjusted.Select(a => "· " + a))
            : null;

        ImportNotImportedMessage = report.NotImported.Count > 0
            ? "Not imported — " + report.NotImported.Count + " rule(s).\n"
              + string.Join("\n", report.NotImported.Select(n => "· " + n))
            : null;

        ImportInfoMessage =
            $"Profile “{sourceLabel}” was validated against schema v1 and this environment's live metadata. "
            + "The app has no JSON or schema editor — imported content appears as rules, counts, and these messages.";

        return report;
    }

    /// <summary>Commits the pending import for the host to push onto the board, then closes back to rules.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInBoard))]
    private void OpenInBoard()
    {
        if (PendingImport is null) return;
        AppliedToBoard = true;
        ShowImportSummary = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the pending import / schema-error pane and returns to the profile list.</summary>
    [RelayCommand(CanExecute = nameof(CanLeaveImportSummary))]
    private void DiscardImport()
    {
        PendingImport = null;
        AppliedToBoard = false;
        ShowImportSummary = false;
        ImportAppliedMessage = null;
        ImportAdjustedMessage = null;
        ImportNotImportedMessage = null;
        ImportInfoMessage = null;
        SchemaErrorMessage = null;
    }

    [RelayCommand]
    private async Task CycleSortAsync()
    {
        _sortMode = (_sortMode + 1) % 3;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task NewProfileAsync()
    {
        var name = await AskNameAsync("new-profile");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _profiles.SaveAsync(new Profile(1, name.Trim(), null, null, []));
            await RefreshAsync();
            SelectedItem = Items.FirstOrDefault(i =>
                string.Equals(i.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    private async Task EditRulesAsync()
    {
        if (SelectedItem is null) return;
        var name = SelectedItem.Name;
        EditRulesRequested?.Invoke(this, name);

        if (_rulesRequest is null || _navigator is null)
            return;

        _rulesRequest.Profile = await _profiles.LoadAsync(name);
        _navigator.Navigate(typeof(RulesPage));
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(CountLabel));
    }

    partial void OnSelectedItemChanged(ProfileListItem? value) => RebuildSelectedDetail();

    private void ApplySearchFilter()
    {
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(CountLabel));
    }

    private void RebuildSelectedDetail()
    {
        SelectedProfileRules.Clear();
        SelectedProfileDescription = "";
        SelectedProfileSeed = "";
        SelectedProfileEditedLabel = "";
        TablesAndVolumeSummary = "";
        ProfileRunHistory = "";
        OnPropertyChanged(nameof(SelectedProfileRuleSummary));

        if (SelectedItem is null)
            return;

        if (!_profilesByName.TryGetValue(SelectedItem.Name, out var profile))
            return;

        SelectedProfileDescription = profile.Description ?? "";
        SelectedProfileSeed = profile.Seed?.ToString(CultureInfo.InvariantCulture) ?? "";
        SelectedProfileEditedLabel = "Local profile";
        ProfileRunHistory = "No runs recorded for this profile.";
        TablesAndVolumeSummary = string.Join(
            "\n",
            profile.Tables.Select(t =>
                $"{t.Table} · {t.Count.ToString("N0", CultureInfo.InvariantCulture)} rows"));

        foreach (var table in profile.Tables)
        {
            if (table.Columns is null) continue;
            foreach (var (column, rule) in table.Columns)
                SelectedProfileRules.Add(new ProfileRuleRow(table.Table, column, OperationSummary(rule)));
        }

        OnPropertyChanged(nameof(SelectedProfileRuleSummary));
    }

    private async Task<string?> AskNameAsync(string suggested)
    {
        if (PromptName is not null)
            return PromptName(suggested);
        if (_dialogs is null)
            return null;

        var box = new System.Windows.Controls.TextBox { Text = suggested };
        var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Profile name",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
        });
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task<bool> ConfirmDeleteAsync(string name)
    {
        if (ConfirmDelete is not null)
            return ConfirmDelete(name);
        if (_dialogs is null)
            return false;

        var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Delete profile",
            Content = $"Delete profile '{name}'? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        });
        return result == ContentDialogResult.Primary;
    }

    private bool CanMutateSelected() => SelectedItem is not null && !ShowImportSummary;

    private bool CanOpenInBoard() => ShowImportSummary && PendingImport is not null && SchemaErrorMessage is null;

    private bool CanLeaveImportSummary() => ShowImportSummary;

    private void SetStatus(string message)
    {
        HasError = false;
        StatusMessage = message;
    }

    private void SetError(string message)
    {
        HasError = true;
        StatusMessage = message;
    }

    private static ProfileListItem Project(Profile profile)
    {
        var tableCount = profile.Tables.Count;
        var ruleCount = 0;
        var rowCount = 0;
        foreach (var table in profile.Tables)
        {
            rowCount += table.Count;
            ruleCount += table.Columns?.Count ?? 0;
        }

        var tableWord = tableCount == 1 ? "table" : "tables";
        var ruleWord = ruleCount == 1 ? "rule" : "rules";
        var summary =
            $"{tableCount} {tableWord} · {ruleCount} {ruleWord} · {rowCount.ToString("N0", CultureInfo.InvariantCulture)} rows";

        return new ProfileListItem(
            profile.Name,
            $"v{profile.ProfileVersion}",
            summary,
            profile.Description,
            profile.Seed,
            ruleCount);
    }

    private static int ParseRowCount(string summary)
    {
        var idx = summary.LastIndexOf('·');
        var tail = idx >= 0 ? summary[(idx + 1)..] : summary;
        tail = tail.Replace("rows", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", "", StringComparison.Ordinal)
            .Trim();
        return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static string OperationSummary(FieldRule rule) => rule switch
    {
        ConstantRule => "constant",
        OneOfRule => "oneOf",
        RangeRule => "range",
        PatternRule => "pattern",
        SequenceRule => "sequence",
        NullRule => "null",
        _ => rule.GetType().Name,
    };
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeedBomb.Core.Rules;
using SeedBomb.Services;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.Views.Pages;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace SeedBomb.ViewModels;

/// <summary>One row in the Profiles list.</summary>
/// <param name="Name">Profile display name.</param>
/// <param name="VersionLabel">e.g. v1.</param>
/// <param name="SummaryLine">e.g. 3 tables · 11 rules · 9,500 rows.</param>
/// <param name="RowCount">Total configured rows, used for sorting independently of display text.</param>
/// <param name="RuleCount">Active column rules.</param>
public sealed record ProfileListItem(
    string Name,
    string VersionLabel,
    string SummaryLine,
    int RowCount,
    int RuleCount = 0);

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
    private readonly IProfileBoard _board;
    private readonly IFileDialogService _files;
    private readonly RulesNavigationRequest? _rulesRequest;
    private readonly IAppNavigator? _navigator;
    private readonly IContentDialogService? _dialogs;
    private readonly Dictionary<string, Profile> _profilesByName = new(StringComparer.OrdinalIgnoreCase);

    private int _sortMode;
    private CancellationTokenSource? _loadCts;

    /// <summary>Initialises the view-model.</summary>
    public ProfilesViewModel(
        IProfileService profiles,
        IProfileBoard board,
        IFileDialogService files,
        RulesNavigationRequest? rulesRequest = null,
        IAppNavigator? navigator = null,
        IContentDialogService? dialogs = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _files = files ?? throw new ArgumentNullException(nameof(files));
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
        catch (Exception ex)
        {
            // WR-005: WPF-UI notifies INavigationAware from `async void PerformNotify`.
            // An escaping exception here is an unhandled crash, not a failed page load.
            SetError($"Couldn't load profiles: {ex.Message}");
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

    /// <summary>Import-summary caption; version comes from <see cref="Profile.CurrentProfileVersion"/>.</summary>
    public string SchemaValidationCaption =>
        $"Validated against schema v{Profile.CurrentProfileVersion} and this environment's live metadata. Results are applied visually — nothing here is editable text.";

    /// <summary>Flattened rules of the selected profile.</summary>
    public ObservableCollection<ProfileRuleRow> SelectedProfileRules { get; } = [];

    /// <summary>Footer rule count for the selected profile.</summary>
    public string SelectedProfileRuleSummary =>
        SelectedItem is null ? "" : $"{SelectedItem.RuleCount} rules";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditRulesCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedProfileRuleSummary))]
    private ProfileListItem? _selectedItem;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    /// <summary>Whether the status InfoBar should show.</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    [ObservableProperty] private bool _hasError;

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private int _selectedDetailTab;

    [ObservableProperty] private string _selectedProfileDescription = "";

    [ObservableProperty] private string _selectedProfileSeed = "";

    [ObservableProperty] private string _selectedProfileEditedLabel = "";

    [ObservableProperty] private string _tablesAndVolumeSummary = "";

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

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasImportApplied))]
    private string? _importAppliedMessage;

    /// <summary>Whether the applied InfoBar should show.</summary>
    public bool HasImportApplied => !string.IsNullOrEmpty(ImportAppliedMessage);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasImportAdjusted))]
    private string? _importAdjustedMessage;

    /// <summary>Whether the adjusted InfoBar should show.</summary>
    public bool HasImportAdjusted => !string.IsNullOrEmpty(ImportAdjustedMessage);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasImportNotImported))]
    private string? _importNotImportedMessage;

    /// <summary>Whether the not-imported InfoBar should show.</summary>
    public bool HasImportNotImported => !string.IsNullOrEmpty(ImportNotImportedMessage);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasImportInfo))]
    private string? _importInfoMessage;

    /// <summary>Whether the info InfoBar should show.</summary>
    public bool HasImportInfo => !string.IsNullOrEmpty(ImportInfoMessage);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSchemaError))]
    private string? _schemaErrorMessage;

    /// <summary>Whether the schema-error InfoBar should show.</summary>
    public bool HasSchemaError => !string.IsNullOrEmpty(SchemaErrorMessage);

    /// <summary>Pending import waiting for Open in board / Discard.</summary>
    public ProfileImportReport? PendingImport { get; private set; }

    /// <summary>Test seam: return true to proceed overwriting a dirty board.</summary>
    public Func<string, bool>? ConfirmOverwrite { get; set; }

    /// <summary>Reloads the profile list from the store.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var names = await _profiles.ListAsync(ct);
        var projected = new List<ProfileListItem>();
        var unreadable = new List<string>();
        _profilesByName.Clear();
        foreach (var name in names)
        {
            Profile profile;
            try
            {
                profile = await _profiles.LoadAsync(name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One corrupt/invalid file (e.g. hand-edited, or from an old bug) must not take
                // the whole list down with it — every other profile is still perfectly loadable.
                unreadable.Add($"{name} ({ex.Message})");
                continue;
            }

            _profilesByName[profile.Name] = profile;
            projected.Add(Project(profile));
        }

        IEnumerable<ProfileListItem> ordered = _sortMode switch
        {
            1 => projected.OrderByDescending(p => p.RowCount),
            _ => projected.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
        };

        var selectedName = SelectedItem?.Name;
        var allItems = ordered.ToList();
        Items.Clear();
        foreach (var item in allItems)
            Items.Add(item);

        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(CountLabel));
        SelectedItem = null;
        SelectedItem = Items.FirstOrDefault(i =>
            string.Equals(i.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(CountLabel));
        RebuildSelectedDetail();

        if (unreadable.Count > 0)
            SetError($"Couldn't load {unreadable.Count} profile(s): {string.Join(", ", unreadable)}");
    }

    /// <summary>Loads the selected (or parameter) profile into the pending-import slot (then Open in board).</summary>
    [RelayCommand]
    private async Task LoadAsync(ProfileListItem? item)
    {
        if (item is not null)
            SelectedItem = item;
        if (SelectedItem is null) return;

        try
        {
            var profile = await _profiles.LoadAsync(SelectedItem.Name);
            await PresentWithMetadataAsync(profile, sourceLabel: SelectedItem.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// CR-005: the one way a profile reaches the pending-import slot, for Load and Import alike —
    /// ask before replacing a dirty board, then validate against live metadata for its tables.
    /// </summary>
    /// <returns><see langword="false"/> when the user kept the board.</returns>
    private async Task<bool> PresentWithMetadataAsync(Profile profile, string sourceLabel, CancellationToken ct)
    {
        if (_board.IsBoardDirty())
        {
            var ok = await ConfirmOverwriteAsync(
                "The rules board has unsaved changes. Load this profile and overwrite the draft?");
            if (!ok) return false;
        }

        await _board.EnsureMetadataAsync(profile.Tables.Select(t => t.Table), ct);
        PresentImport(profile, sourceLabel);
        return true;
    }

    /// <summary>Exports the selected profile to a user-chosen path.</summary>
    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    private async Task ExportAsync()
    {
        if (SelectedItem is null) return;
        var dest = _files.PickProfileExportPath(SelectedItem.Name);
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
        var suggested = $"{SelectedItem.Name}-copy";
        if (_dialogs is not null)
        {
            newName = await AskNameAsync(suggested);
            if (string.IsNullOrWhiteSpace(newName)) return;
        }
        else
            newName = suggested;

        if (newName is null || !JsonProfileService.IsValidName(newName)) return;

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

    /// <summary>
    /// Import from file: schema stage (Task 10) then metadata stage → visual summary only.
    /// </summary>
    [RelayCommand]
    private async Task ImportFromFileAsync()
    {
        var path = _files.PickProfileToImport();
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

        Profile? profile;
        string? error;
        try
        {
            (profile, error) = await _profiles.ImportAsync(sourcePath, ct);
            if (profile is null && error is { } conflict
                && conflict.StartsWith("conflict:", StringComparison.Ordinal))
            {
                // WR-014: importing over a same-name profile is a real overwrite — ask, the same
                // way Load already asks before replacing a dirty board.
                var ok = await ConfirmOverwriteAsync(conflict["conflict:".Length..].Trim());
                if (!ok) return;
                (profile, error) = await _profiles.ImportAsync(sourcePath, ct, allowOverwrite: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // WR-006: a locked or read-only source file must land in the import summary, not the
            // generic crash box.
            SchemaErrorMessage = ex.Message;
            ShowImportSummary = true;
            return;
        }

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

        try
        {
            await RefreshAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError(ex.Message);
            return;
        }

        try
        {
            // CR-005: the same path as Load — live metadata for its tables and the dirty-board prompt.
            if (!await PresentWithMetadataAsync(profile, Path.GetFileName(sourcePath), ct))
                SetStatus($"Imported “{profile.Name}”.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// Test entry: metadata-validate an already-parsed profile (after <see cref="IProfileService.ImportAsync"/>).
    /// </summary>
    public ProfileImportReport PresentImport(Profile profile, string sourceLabel)
    {
        var report = ProfileImport.ValidateAgainstMetadata(profile, _board.EntityMetadataMap, _board.RunId);
        PendingImport = report;
        SchemaErrorMessage = null;
        ShowImportSummary = true;

        ImportAppliedMessage = report.AppliedRuleCount > 0 || report.AppliedTableSummaries.Count > 0
            ? $"Applied — {report.AppliedRuleCount} rule(s) across {report.AppliedTableSummaries.Count} table(s). "
              + string.Join(", ", report.AppliedTableSummaries)
              + ". Record counts adopted from the profile."
            : null;

        ImportAdjustedMessage = report.Adjusted.Count > 0
            // Not all of these are clamps — the bucket carries every validator warning, including
            // risky-value rules that are applied as authored and re-confirmed when the run starts.
            ? "Warnings — " + report.Adjusted.Count + " rule(s). All were applied; see below.\n"
              + string.Join("\n", report.Adjusted.Select(a => "· " + a))
            : null;

        ImportNotImportedMessage = report.NotImported.Count > 0
            ? "Not imported — " + report.NotImported.Count + " rule(s).\n"
              + string.Join("\n", report.NotImported.Select(n => "· " + n))
            : null;

        ImportInfoMessage =
            $"Profile “{sourceLabel}” was validated against schema v{Profile.CurrentProfileVersion} and this environment's live metadata. "
            + "The app has no JSON or schema editor — imported content appears as rules, counts, and these messages.";

        return report;
    }

    /// <summary>Loads the pending import onto the Generate board and opens Generate.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInBoard))]
    private void OpenInBoard()
    {
        if (PendingImport is null) return;
        _board.ApplyImportReport(PendingImport);
        ShowImportSummary = false;
        _navigator?.Navigate(typeof(GeneratePage));
    }

    /// <summary>Drops the pending import / schema-error pane and returns to the profile list.</summary>
    [RelayCommand(CanExecute = nameof(CanLeaveImportSummary))]
    private void DiscardImport()
    {
        PendingImport = null;
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
        _sortMode = (_sortMode + 1) % 2;
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError(ex.Message);
        }
    }

    // Reusing the board snapshot guarantees a schema-valid profile: BuildProfileSnapshot falls
    // back to a placeholder "account" table when nothing is selected, so this never produces
    // an empty table list — which LoadAsync's schema check would reject on the next refresh.
    [RelayCommand]
    private async Task NewProfileAsync()
    {
        var name = await AskNameAsync("new-profile");
        if (name is null || !JsonProfileService.IsValidName(name)) return;

        try
        {
            var profile = _board.BuildProfileSnapshot(name.Trim());
            await _profiles.SaveAsync(profile);
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

    [RelayCommand(CanExecute = nameof(CanMutateSelected))]
    private async Task EditRulesAsync()
    {
        if (SelectedItem is null) return;
        if (_rulesRequest is null || _navigator is null)
            return;

        Profile profile;
        try
        {
            profile = await _profiles.LoadAsync(SelectedItem.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError(ex.Message);
            return;
        }

        // Generate stamps TableName/OnSaved/ReturnPage every time it navigates away. Replace all of
        // it, or Back returns to Generate and a save overwrites the board with this profile.
        _rulesRequest.Clear();
        _rulesRequest.Profile = profile;
        _rulesRequest.IsStored = true; // CR-003: rule edits go back to this profile's file
        _rulesRequest.OnSaved = _board.ApplySavedProfileIfActive;
        _navigator.Navigate(typeof(RulesPage));
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(CountLabel));
    }

    partial void OnSelectedItemChanged(ProfileListItem? value) => RebuildSelectedDetail();

    private void RebuildSelectedDetail()
    {
        SelectedProfileRules.Clear();
        SelectedProfileDescription = "";
        SelectedProfileSeed = "";
        SelectedProfileEditedLabel = "";
        TablesAndVolumeSummary = "";
        OnPropertyChanged(nameof(SelectedProfileRuleSummary));

        if (SelectedItem is null)
            return;

        if (!_profilesByName.TryGetValue(SelectedItem.Name, out var profile))
            return;

        SelectedProfileDescription = profile.Description ?? "";
        SelectedProfileSeed = profile.Seed?.ToString(CultureInfo.InvariantCulture) ?? "";
        SelectedProfileEditedLabel = "Local profile";
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

    private async Task<string?> AskNameAsync(string suggested) =>
        _dialogs is null ? null : await _dialogs.AskProfileNameAsync(suggested);

    // WR-020: the themed dialog is the production path; the seam stays for tests.
    private async Task<bool> ConfirmOverwriteAsync(string message)
    {
        if (ConfirmOverwrite is not null)
            return ConfirmOverwrite(message);
        return _dialogs is not null
            && await _dialogs.ConfirmAsync("Load profile", message, "Overwrite");
    }

    private async Task<bool> ConfirmDeleteAsync(string name)
    {
        if (_dialogs is null)
            return false;

        return await _dialogs.ConfirmAsync("Delete profile", $"Delete profile '{name}'? This cannot be undone.", "Delete");
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
            rowCount,
            ruleCount);
    }

    private static string OperationSummary(FieldRule rule) => rule switch
    {
        ConstantRule => "constant",
        OneOfRule => "oneOf",
        RangeRule => "range",
        PatternRule => "pattern",
        SequenceRule => "sequence",
        NullRule => "null",
        BogusRule b => $"bogus · {b.Api}.{b.Endpoint}",
        LookupRandomRule => "lookupRandom",
        _ => rule.GetType().Name,
    };
}
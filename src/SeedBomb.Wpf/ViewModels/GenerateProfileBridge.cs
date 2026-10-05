using SeedBomb.Core.Contracts;
using SeedBomb.Core.Rules;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.ViewModels.Controls;
using SeedBomb.Views.Pages;

namespace SeedBomb.ViewModels;

/// <summary>
/// Profile snapshot, import apply, rules-page handoff, and draft restore for
/// <see cref="GenerateViewModel"/>. Returns applied state through the owner; the owner surfaces it.
/// </summary>
internal sealed class GenerateProfileBridge
{
    private readonly GenerateViewModel _owner;
    private readonly IProfileService _profiles;
    private readonly RulesNavigationRequest? _rulesRequest;
    private Profile? _restoredDraft;

    /// <summary>Initialises the profile-bridge collaborator.</summary>
    public GenerateProfileBridge(
        GenerateViewModel owner,
        IProfileService profiles,
        RulesNavigationRequest? rulesRequest)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(profiles);
        _owner = owner;
        _profiles = profiles;
        _rulesRequest = rulesRequest;
    }

    /// <summary>Loads the autosaved draft and stamps its seed onto the wizard when present.</summary>
    public async Task LoadDraftAsync()
    {
        try
        {
            _restoredDraft = await _profiles.LoadDraftAsync();
            if (_restoredDraft?.Seed is int seed)
                _owner.Seed = seed;
        }
        catch (Exception ex)
        {
            _owner.ReportDraftFailure("restore", ex);
            _restoredDraft = null;
        }
    }

    /// <summary>Deletes the autosaved draft. Failures go to <see cref="GenerateViewModel.ReportDraftFailure"/>.</summary>
    public async Task ClearDraftAsync()
    {
        try
        {
            await _profiles.ClearDraftAsync();
        }
        catch (Exception ex)
        {
            _owner.ReportDraftFailure("clear", ex);
        }
    }

    /// <summary>Drops a restored draft and the in-flight rules-page payload.</summary>
    public void ResetSession()
    {
        _restoredDraft = null;
        _rulesRequest?.Clear();
    }

    /// <summary>Stamps an in-memory working-set snapshot for a Rules-page edit. Does not persist.</summary>
    /// <returns><see langword="false"/> when no rules-navigation payload is wired.</returns>
    public bool TryPrepareRulesEdit()
    {
        if (_rulesRequest is null)
            return false;

        _rulesRequest.Clear(); // CR-004: stamp a whole payload, never add to a stale one
        _rulesRequest.Profile = BuildProfileSnapshot(
            string.Equals(_owner.ActiveProfileName, "No profile loaded", StringComparison.Ordinal)
                ? "working-set"
                : _owner.ActiveProfileName);
        _rulesRequest.TableName = _owner.SelectedEntities.FirstOrDefault()?.LogicalName;
        _rulesRequest.OnSaved = ApplySavedRulesProfile;
        _rulesRequest.ReturnPage = typeof(GeneratePage);
        return true;
    }

    private Profile? _savedDuringRun;

    /// <summary>Applies the last Rules save held back while Generate was running (WR-009).</summary>
    public void ApplyRulesSavedDuringRun()
    {
        if (_savedDuringRun is not { } profile)
            return;
        _savedDuringRun = null;
        ApplySavedRulesProfile(profile);
    }

    /// <summary>Applies a profile returned from the Rules page onto the wizard board.</summary>
    public void ApplySavedRulesProfile(Profile profile)
    {
        // WR-009: both Rules-save routes land here. While Generate runs, hold the save instead of
        // renaming the profile and dropping tables under the run; GenerateAsync applies it after.
        if (_owner.IsRunning)
        {
            _savedDuringRun = profile;
            return;
        }

        _owner.ActiveProfileName = profile.Name;
        // The Rules page can remove tables; SelectReportTables only ever adds them.
        var kept = _owner.SelectedEntities
            .Where(e => profile.Tables.Any(t => string.Equals(t.Table, e.LogicalName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (kept.Count < _owner.SelectedEntities.Count)
        {
            _owner.OnEntitiesChanged(kept);
            _owner.EntitySelector?.SetSelection(kept);
        }

        if (_owner.LiveEntityMetadata.Count > 0)
        {
            var report = ProfileImport.ValidateAgainstMetadata(profile, _owner.LiveEntityMetadata, _owner.RunId);
            ApplyImportReport(report);
            return;
        }

        if (profile.Seed is int seed)
            _owner.Seed = seed;

        foreach (var table in profile.Tables)
            _owner.FieldOverrides?.SetCount(table.Table, table.Count);

        var draft = new Dictionary<string, Dictionary<string, RuleDraftEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in profile.Tables)
        {
            if (table.Columns is null || table.Columns.Count == 0)
                continue;

            var cols = new Dictionary<string, RuleDraftEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, rule) in table.Columns)
                cols[column] = new RuleDraftEntry(rule);
            draft[table.Table] = cols;
        }

        _owner.FieldRules.ReplaceDraft(draft);
    }

    /// <summary>Builds a profile snapshot of the current wizard selection, counts, rules, and seed.</summary>
    public Profile BuildProfileSnapshot(string name)
    {
        var counts = _owner.FieldOverrides?.GetCounts() ?? new Dictionary<string, int>();
        var rules = _owner.FieldRules?.GetRules()
                    ?? new Dictionary<string, Dictionary<string, FieldRule>>(StringComparer.OrdinalIgnoreCase);

        var tables = new List<ProfileTable>();
        foreach (var entity in _owner.SelectedEntities)
        {
            Dictionary<string, FieldRule>? cols = null;
            if (rules.TryGetValue(entity.LogicalName, out var r) && r.Count > 0)
                cols = new Dictionary<string, FieldRule>(r, StringComparer.OrdinalIgnoreCase);
            tables.Add(new ProfileTable(
                entity.LogicalName,
                counts.GetValueOrDefault(entity.LogicalName, _owner.DefaultRecordCount),
                cols));
        }

        if (tables.Count == 0)
            tables.Add(new ProfileTable("account", _owner.DefaultRecordCount, null));

        return new Profile(1, name, Description: null, _owner.Seed, tables);
    }

    /// <summary>
    /// Ensures every table named by an applied import report is present in
    /// <see cref="GenerateViewModel.SelectedEntities"/> — a set-union/upsert against the current
    /// selection, so tables already selected are left exactly where they are (no duplicates, no
    /// reordering). Tables the report names but this run has no metadata for are skipped: there is
    /// no <see cref="EntitySummary"/> to build for them and counts/rules for such tables never
    /// reach the board anyway (see <see cref="ProfileImport"/>).
    /// </summary>
    /// <param name="tableNames">Logical names of tables named by the report.</param>
    private void SelectReportTables(IEnumerable<string> tableNames)
    {
        var merged = _owner.SelectedEntities.ToList();
        var seen = new HashSet<string>(merged.Select(e => e.LogicalName), StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var table in tableNames)
        {
            if (seen.Contains(table) || !_owner.LiveEntityMetadata.TryGetValue(table, out var meta) ||
                meta.LogicalName is null)
                continue;

            merged.Add(new EntitySummary(
                meta.LogicalName,
                meta.DisplayName?.UserLocalizedLabel?.Label ?? meta.LogicalName,
                EntitySummary.IsUserCreated(meta.LogicalName, meta.IsCustomEntity == true)));
            seen.Add(meta.LogicalName);
            changed = true;
        }

        if (!changed)
            return;

        _owner.OnEntitiesChanged(merged);
        _owner.EntitySelector?.SetSelection(merged);
    }

    /// <summary>Pushes a metadata-validated import report onto the board (EffectiveRules only).</summary>
    public void ApplyImportReport(ProfileImportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // The Profiles page applies a report directly (no ApplySavedRulesProfile hop), so the
        // Profile card's name has to come off the report or it stays "No profile loaded".
        if (!string.IsNullOrEmpty(report.ProfileName))
            _owner.ActiveProfileName = report.ProfileName;

        if (report.Seed is int seed)
            _owner.Seed = seed;

        // Select the report's tables first: FieldOverridesViewModel.SetCount() is a no-op for a
        // table that isn't a selected entry yet, so the counts loop below would silently drop
        // counts for tables the board doesn't already have selected.
        SelectReportTables(report.TableCounts.Keys);

        foreach (var (table, count) in report.TableCounts)
            _owner.FieldOverrides?.SetCount(table, count);

        if (_owner.FieldRules is null)
            return;

        var draft = new Dictionary<string, Dictionary<string, RuleDraftEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, columns) in report.BoardRules)
        {
            var tableDraft = new Dictionary<string, RuleDraftEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, rule) in columns)
                tableDraft[column] = new RuleDraftEntry(rule);

            if (tableDraft.Count > 0)
                draft[table] = tableDraft;
        }

        _owner.FieldRules.ReplaceDraft(draft);
    }

    /// <summary>Applies a draft restored at navigation time once live metadata is loaded.</summary>
    public void TryApplyRestoredDraft()
    {
        if (_restoredDraft is null || _owner.FieldRules is null || _owner.LiveEntityMetadata.Count == 0)
            return;

        var hasTables = _restoredDraft.Tables.Count > 0;
        var report = ProfileImport.ValidateAgainstMetadata(_restoredDraft, _owner.LiveEntityMetadata, _owner.RunId);
        ApplyImportReport(report);
        _restoredDraft = null;
        if (hasTables)
            _owner.CurrentStep = 1;
    }
}
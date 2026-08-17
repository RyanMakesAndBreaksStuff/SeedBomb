using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Generators;
using DataGen.Core.Metadata;
using DataGen.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Profiles;
using Seedbomb.Views.Pages;

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

/// <summary>
/// ViewModel for the rule editor dialog — the one place settable vs. platform-owned columns
/// are enumerated (S3). Bodies delegate entirely to <see cref="RuleEligibility"/>,
/// <see cref="RuleValidator"/>, and <see cref="RuleValueGenerator"/> (Wave 1, DataGen.Core.Rules);
/// this class owns no eligibility, validation, or evaluation semantics of its own.
/// </summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    // ── §3.1 Applies-to catalogs (Docs/field-rules-proto.html, table 3.1) ───────
    private static readonly string[] TextOps = ["constant", "oneOf", "pattern", "null"];
    private static readonly string[] NumericOps = ["constant", "oneOf", "range", "sequence", "null"]; // Whole#/BigInt/Decimal/Money
    private static readonly string[] FloatOps = ["constant", "oneOf", "range", "null"]; // Float has no sequence row
    private static readonly string[] DateOps = ["constant", "oneOf", "range", "null"];
    private static readonly string[] ChoiceOps = ["constant", "oneOf", "null"]; // Choice/Status(reason)/Two-Options
    private static readonly string[] NoOps = [];

    private readonly Dictionary<string, AttributeMetadata> _byName;
    private readonly List<PickerColumn> _allSettable;
    private readonly List<PickerColumn> _allExcluded;
    private readonly Dictionary<string, EntityMetadata> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMetadataProvider? _metadata;
    private readonly IProfileService? _profiles;
    private readonly IAppNavigator? _navigator;
    private readonly RulesNavigationRequest? _request;

    private string _table;
    private int _recordCount;
    private int _seed;
    private string _runId;
    private string _filterKey = "All";
    private int _previewSalt;
    private bool _suppressTableChange;
    private Profile? _profile;
    private Action<Profile>? _onSaved;
    private Type? _returnPage;

    private IReadOnlyList<RuleMessage> _messages = [];
    private IReadOnlyList<string> _previewValues = [];
    private FieldRule? _effectiveRule;
    private FieldRule? _restoredBogus;

    /// <summary>Initialises the editor from full live entity metadata (Task 9 supplies this via <c>IMetadataProvider</c>).</summary>
    /// <param name="meta">Full entity metadata — editor never derives columns from <c>EntitySummary</c> or creates a provider.</param>
    /// <param name="recordCount">Planned record count for the run (sequence overflow / pattern worst-case width).</param>
    /// <param name="seed">Generation seed — feeds the deterministic preview substream.</param>
    /// <param name="runId">Run id — feeds <c>{runId}</c> pattern expansion and its worst-case length.</param>
    public RuleEditorViewModel(EntityMetadata meta, int recordCount, int seed, string runId)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(runId);

        _byName = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);
        _allSettable = [];
        _allExcluded = [];
        _table = string.Empty;
        _recordCount = recordCount;
        _seed = seed;
        _runId = runId;
        ResetFromMetadata(meta);
    }

    /// <summary>DI constructor for the Rules page. Call <see cref="LoadForProfileAsync"/> on navigate.</summary>
    [ActivatorUtilitiesConstructor]
    public RuleEditorViewModel(
        IMetadataProvider metadata,
        IProfileService profiles,
        IAppNavigator navigator,
        RulesNavigationRequest request)
    {
        _metadata = metadata;
        _profiles = profiles;
        _navigator = navigator;
        _request = request;
        _byName = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);
        _allSettable = [];
        _allExcluded = [];
        _table = string.Empty;
        _recordCount = 10;
        _seed = 42;
        _runId = "rules-preview";
    }

    // ── Handoff aliases (lock 22) ────────────────────────────────────────────

    /// <summary>Handoff alias for <see cref="SearchText"/>.</summary>
    public string ColumnFilter
    {
        get => SearchText;
        set => SearchText = value;
    }

    /// <summary>Handoff alias for <see cref="AvailableOps"/>.</summary>
    public IReadOnlyList<string> AvailableOperations => AvailableOps;

    /// <summary>Handoff alias for <see cref="SelectedOp"/>.</summary>
    public string SelectedOperation
    {
        get => SelectedOp;
        set => SelectedOp = value;
    }

    /// <summary>Handoff alias for <see cref="Template"/>.</summary>
    public string TemplateExpression
    {
        get => Template;
        set => Template = value;
    }

    /// <summary>True when the selected op is <c>pattern</c> — shows the template block.</summary>
    public bool IsTemplateOperation => SelectedOp == "pattern";

    // ── Page-scoped surface ──────────────────────────────────────────────────

    [ObservableProperty]
    private string _profileName = "Untitled";

    /// <summary>Breadcrumb root. Generate when opened from the wizard; Profiles otherwise.</summary>
    public string BreadcrumbRootLabel =>
        _returnPage == typeof(GeneratePage) ? "Generate" : "Profiles";

    [ObservableProperty]
    private RuleTableOption? _selectedTable;

    /// <summary>Tables in the current profile (header switcher).</summary>
    public ObservableCollection<RuleTableOption> Tables { get; } = [];

    /// <summary>Mapped / Required / All chips.</summary>
    public ObservableCollection<ColumnFilterMode> ColumnFilterModes { get; } = [];

    /// <summary>Live preview rows mapped from <see cref="PreviewValues"/>.</summary>
    public ObservableCollection<PreviewRow> PreviewRows { get; } = [];

    /// <summary>Insertable pattern tokens.</summary>
    public ObservableCollection<TokenChip> AvailableTokens { get; } = [new("{seq}"), new("{runId}"), new("{n}")];

    /// <summary>Collision strategies. Bind-only — Core has no FieldRule member (lock 16).</summary>
    public IReadOnlyList<string> CollisionStrategies { get; } = ["Keep first"];

    /// <summary>Case transforms. Bind-only — Core has no FieldRule member (lock 16).</summary>
    public IReadOnlyList<string> CaseTransforms { get; } = ["None"];

    [ObservableProperty]
    private string _selectedCollisionStrategy = "Keep first";

    [ObservableProperty]
    private string _selectedCaseTransform = "None";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlankRateLabel))]
    private bool _allowBlanks;

    /// <summary>Derived AllowBlanks copy. Bind-only — no Core effect (lock 16).</summary>
    public string BlankRateLabel => AllowBlanks ? "Leave blank for some rows" : "Never leave blank";

    [ObservableProperty]
    private bool _hasDependencyNote;

    [ObservableProperty]
    private string _dependencyNote = "";

    /// <summary>Preview pane footer — sampled from the table's planned row count.</summary>
    public string PreviewFooterLabel => $"Sampled from {_recordCount:N0} rows";

    /// <summary>Grouped view. Null after the metadata ctor — tests must not touch it.</summary>
    public ICollectionView? ColumnsView { get; private set; }

    // ── Picker ────────────────────────────────────────────────────────────────

    /// <summary>Eligible rule targets (RuleEligibility.Classify == Settable), filtered by <see cref="SearchText"/>.</summary>
    public IReadOnlyList<PickerColumn> SettableColumns => Filter(_allSettable);

    /// <summary>Platform-owned columns, grouped and disabled with their reason; stays findable while excluded (S3).</summary>
    public IReadOnlyList<PickerColumn> ExcludedColumns => Filter(_allExcluded);

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(ColumnFilter));
        OnPropertyChanged(nameof(SettableColumns));
        OnPropertyChanged(nameof(ExcludedColumns));
        ColumnsView?.Refresh();
    }

    [ObservableProperty]
    private PickerColumn? _selectedColumn;

    partial void OnSelectedColumnChanged(PickerColumn? value)
    {
        Options.Clear();
        if (value is not null && _byName.TryGetValue(value.LogicalName, out var attr) && attr is EnumAttributeMetadata em)
        {
            foreach (var opt in em.OptionSet?.Options ?? [])
            {
                if (opt.Value is int v)
                {
                    var choice = new OptionChoice(v, opt.Label?.UserLocalizedLabel?.Label ?? v.ToString());
                    choice.PropertyChanged += (_, _) => Revalidate();
                    Options.Add(choice);
                }
            }
        }

        OnPropertyChanged(nameof(AvailableOps));
        OnPropertyChanged(nameof(AvailableOpOptions));
        OnPropertyChanged(nameof(AvailableOperations));
        SelectedOp = AvailableOps.FirstOrDefault() ?? string.Empty;
        if (value is not null
            && TryGetProfileColumns(out var cols)
            && cols.TryGetValue(value.LogicalName, out var existing))
        {
            ApplyExistingRule(existing);
        }

        Revalidate();
    }

    /// <summary>Ops valid for <see cref="SelectedColumn"/>'s type, per §3.1 Applies-to.</summary>
    public IReadOnlyList<string> AvailableOps =>
        SelectedColumn is not null && _byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
            ? OpsFor(attr)
            : NoOps;

    /// <summary>Op cards for the editor UI — same set as <see cref="AvailableOps"/> with display copy.</summary>
    public IReadOnlyList<OpOption> AvailableOpOptions => AvailableOps.Select(ToOpOption).ToList();

    // ── Op selection + typed parameters ──────────────────────────────────────

    [ObservableProperty]
    private string _selectedOp = string.Empty;

    partial void OnSelectedOpChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedOperation));
        OnPropertyChanged(nameof(IsTemplateOperation));
        Revalidate();
    }

    [ObservableProperty]
    private string _constantText = string.Empty;
    partial void OnConstantTextChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _template = string.Empty;
    partial void OnTemplateChanged(string value)
    {
        OnPropertyChanged(nameof(TemplateExpression));
        Revalidate();
    }

    [ObservableProperty]
    private string _minText = string.Empty;
    partial void OnMinTextChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _maxText = string.Empty;
    partial void OnMaxTextChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _startText = string.Empty;
    partial void OnStartTextChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _stepText = string.Empty;
    partial void OnStepTextChanged(string value) => Revalidate();

    /// <summary>Checkable option subset for Choice/Two-Options <c>oneOf</c> rule.</summary>
    public ObservableCollection<OptionChoice> Options { get; } = [];

    [ObservableProperty]
    private OneOfPick _pick = OneOfPick.Random;
    partial void OnPickChanged(OneOfPick value) => Revalidate();

    // ── Live validation + preview (RuleValidator / RuleValueGenerator — Wave 1) ─

    /// <summary>Live <see cref="RuleValidator"/> output for the current draft rule.</summary>
    public IReadOnlyList<RuleMessage> Messages => _messages;

    /// <summary>True when <see cref="Messages"/> is non-empty — drives the editor's <c>ui:InfoBar</c>.</summary>
    public bool HasMessages => _messages.Count > 0;

    /// <summary>First message's text, shown in the editor's <c>ui:InfoBar</c>.</summary>
    public string InfoBarMessage => _messages.Count > 0 ? _messages[0].Text : string.Empty;

    /// <summary>True when any current message is Error-severity — selects the InfoBar's Error styling.</summary>
    public bool InfoBarIsError => _messages.Any(m => m.Severity == RuleMessageSeverity.Error);

    /// <summary><see cref="RuleValueGenerator"/> output for rows 0..2 of the current effective rule.</summary>
    public IReadOnlyList<string> PreviewValues => _previewValues;

    /// <summary>True when a draft rule built and validated with no Error-severity message.</summary>
    public bool CanSave => _effectiveRule is not null && !_messages.Any(m => m.Severity == RuleMessageSeverity.Error);

    /// <summary>Returns the last-validated effective rule (clamped range, etc.), or null if nothing valid is drafted.</summary>
    public FieldRule? BuildRule() => _effectiveRule;

    /// <summary>
    /// Loads an existing board rule into the editor after <see cref="SelectedColumn"/> is set.
    /// Restores op + typed parameters so edit mode does not always fall back to the default op.
    /// </summary>
    public void ApplyExistingRule(FieldRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        _restoredBogus = null;
        switch (rule)
        {
            case ConstantRule c:
                SelectedOp = "constant";
                ConstantText = JsonElementToEditorText(c.Value);
                break;
            case OneOfRule o:
                SelectedOp = "oneOf";
                Pick = o.Pick;
                ApplyOneOfValues(o.Values);
                break;
            case RangeRule r:
                SelectedOp = "range";
                MinText = JsonElementToEditorText(r.Min);
                MaxText = JsonElementToEditorText(r.Max);
                break;
            case PatternRule p:
                SelectedOp = "pattern";
                Template = p.Template;
                break;
            case SequenceRule s:
                SelectedOp = "sequence";
                StartText = s.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                StepText = s.Step.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case NullRule:
                SelectedOp = "null";
                break;
            case BogusRule b:
                _restoredBogus = b;
                SelectedOp = "bogus";
                break;
        }
    }

    /// <summary>True when <paramref name="column"/> passes the active search + chip filter.</summary>
    public bool MatchesColumnFilter(PickerColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (!string.IsNullOrWhiteSpace(SearchText)
            && !column.LogicalName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            && !column.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _filterKey switch
        {
            "Required" => IsRequired(column),
            // Metadata ctor has no profile: keep settable columns visible so required-unmapped
            // (and the rest of the picker) stay testable without a ColumnsView.
            "Mapped" => IsMapped(column) || IsRequired(column) || _profile is null,
            _ => true,
        };
    }

    /// <summary>
    /// Reads <see cref="RulesNavigationRequest"/>, then loads tables + metadata.
    /// Creates <see cref="ColumnsView"/> only on this path (lock 24).
    /// </summary>
    public async Task LoadForProfileAsync(CancellationToken ct = default)
    {
        _onSaved = _request?.OnSaved;
        _returnPage = _request?.ReturnPage;
        OnPropertyChanged(nameof(BreadcrumbRootLabel));
        var profile = _request?.Profile;
        var tableName = _request?.TableName;
        _request?.Clear();

        if (profile is null)
        {
            _profile = null;
            _onSaved = null;
            ProfileName = "No profile selected";
            _suppressTableChange = true;
            try
            {
                Tables.Clear();
                SelectedTable = null;
            }
            finally
            {
                _suppressTableChange = false;
            }

            SelectedColumn = null;
            _allSettable.Clear();
            _allExcluded.Clear();
            OnPropertyChanged(nameof(SettableColumns));
            OnPropertyChanged(nameof(ExcludedColumns));
            ColumnFilterModes.Clear();
            ColumnsView = null;
            OnPropertyChanged(nameof(ColumnsView));
            SaveProfileCommand.NotifyCanExecuteChanged();
            return;
        }

        ct.ThrowIfCancellationRequested();

        _profile = profile;
        ProfileName = string.IsNullOrWhiteSpace(profile.Name) ? "Untitled" : profile.Name;
        if (profile.Seed is int seed)
            _seed = seed;

        _suppressTableChange = true;
        try
        {
            Tables.Clear();
            foreach (var table in profile.Tables)
                Tables.Add(new RuleTableOption(table.Table, table.Table));

            SelectedTable = Tables.FirstOrDefault(t =>
                    string.Equals(t.LogicalName, tableName, StringComparison.OrdinalIgnoreCase))
                ?? Tables.FirstOrDefault();
        }
        finally
        {
            _suppressTableChange = false;
        }

        if (SelectedTable is null || _metadata is null)
        {
            SaveProfileCommand.NotifyCanExecuteChanged();
            return;
        }

        ApplyTableCounts(SelectedTable.LogicalName);

        var names = Tables.Select(t => t.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var list = await _metadata.GetEntitiesAsync(names, ct);
        _entities.Clear();
        foreach (var entity in list)
        {
            if (entity.LogicalName is not null)
                _entities[entity.LogicalName] = entity;
        }

        if (!_entities.TryGetValue(SelectedTable.LogicalName, out var meta))
        {
            SaveProfileCommand.NotifyCanExecuteChanged();
            return;
        }

        ResetFromMetadata(meta);

        ColumnsView = CollectionViewSource.GetDefaultView(_allSettable);
        ColumnsView.Filter = o => o is PickerColumn c && MatchesColumnFilter(c);
        if (ColumnsView is CollectionView view)
        {
            using (view.DeferRefresh())
            {
                view.GroupDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerColumn.GroupName)));
            }
        }

        OnPropertyChanged(nameof(ColumnsView));
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTableChanged(RuleTableOption? value)
    {
        if (_suppressTableChange || value is null)
            return;

        ApplyTableCounts(value.LogicalName);
        if (!_entities.TryGetValue(value.LogicalName, out var meta))
            return;

        ResetFromMetadata(meta);
        ColumnsView?.Refresh();
        OnPropertyChanged(nameof(PreviewFooterLabel));
    }

    [RelayCommand]
    private void SetColumnFilterMode(ColumnFilterMode? mode)
    {
        if (mode is null || string.IsNullOrWhiteSpace(mode.Key))
            return;

        _filterKey = mode.Key;
        if (ColumnsView is CollectionView view)
        {
            using (view.DeferRefresh())
            {
                view.GroupDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerColumn.GroupName)));
            }
        }

        ColumnsView?.Refresh();
    }

    [RelayCommand]
    private void InsertToken(TokenChip? token)
    {
        if (token is null)
            return;
        Template += token.Name;
    }

    [RelayCommand]
    private void RerollPreview()
    {
        _previewSalt++;
        Revalidate();
    }

    [RelayCommand]
    private void NavigateToProfiles() => _navigator?.Navigate(_returnPage ?? typeof(ProfilesPage));

    private bool CanSaveProfile() => _profiles is not null && _profile is not null && CanSave;

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync(CancellationToken ct)
    {
        if (_profiles is null || _profile is null || SelectedColumn is null)
            return;

        var rule = BuildRule();
        if (rule is null)
            return;

        var tables = _profile.Tables.ToList();
        var idx = tables.FindIndex(t => string.Equals(t.Table, _table, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return;

        var cols = tables[idx].Columns ?? new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase);
        cols[SelectedColumn.LogicalName] = rule;
        tables[idx] = tables[idx] with { Columns = cols };
        _profile = _profile with { Tables = tables };

        // Generate working-set snapshots are not in the store — callback only, no disk write.
        var names = await _profiles.ListAsync(ct);
        if (names.Any(n => string.Equals(n, _profile.Name, StringComparison.OrdinalIgnoreCase)))
            await _profiles.SaveAsync(_profile, ct);

        _onSaved?.Invoke(_profile);

        if (_returnPage is not null)
            _navigator?.Navigate(_returnPage);

        if (_entities.TryGetValue(_table, out var meta))
        {
            ResetFromMetadata(meta);
            ColumnsView?.Refresh();
        }
    }

    private void ApplyOneOfValues(IReadOnlyList<JsonElement> values)
    {
        if (Options.Count > 0)
        {
            var wanted = values
                .Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
                    : v.ValueKind == JsonValueKind.True ? 1
                    : v.ValueKind == JsonValueKind.False ? 0
                    : (int?)null)
                .Where(i => i.HasValue)
                .Select(i => i!.Value)
                .ToHashSet();
            foreach (var opt in Options)
                opt.IsChecked = wanted.Contains(opt.Value);
            return;
        }

        // Non-choice: comma-separated literal list reuses ConstantText.
        ConstantText = string.Join(", ", values.Select(JsonElementToEditorText));
    }

    private static string JsonElementToEditorText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => el.GetRawText(),
        _ => el.GetRawText(),
    };

    private static OpOption ToOpOption(string op) => op switch
    {
        "constant" => new("constant", "constant", "fixed value on every row"),
        "oneOf" => new("oneOf", "one-of", "subset of options / literals"),
        "range" => new("range", "range", "uniform pick within bounds"),
        "pattern" => new("pattern", "pattern", "text template with tokens"),
        "sequence" => new("sequence", "sequence", "start + step per row"),
        "null" => new("null", "null", "leave unset (platform default)"),
        _ => new(op, op, string.Empty),
    };

    private void Revalidate()
    {
        List<RuleMessage> messages = [];
        List<string> preview = [];
        _effectiveRule = null;

        if (SelectedColumn is not null
            && _byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
            && !string.IsNullOrEmpty(SelectedOp))
        {
            var draft = TryBuildDraft(attr, SelectedOp);
            if (draft is null)
            {
                messages.Add(new RuleMessage(RuleMessageSeverity.Error, "Enter a value for this rule."));
            }
            else
            {
                var result = RuleValidator.Validate(
                    draft, attr, new RuleValidationContext(_table, _recordCount, _runId));
                messages = result.Messages.ToList();
                if (result.IsValid && result.EffectiveRule is not null)
                {
                    _effectiveRule = result.EffectiveRule;
                    var previewSeed = _seed + _previewSalt;
                    var eval = new RuleEvaluationContext(_table, previewSeed, DeterministicFaker.DefaultLocale, _runId, _recordCount);
                    if (result.EffectiveRule is BogusRule bogus)
                    {
                        var prepared = BogusRulePreparer.CompileRule(bogus, attr, eval);
                        using var session = new BogusEvaluatorSession(eval.Locale);
                        for (var row = 0; row < 3; row++)
                            preview.Add(FormatPreview(session.Evaluate(prepared, attr, eval, row)));
                    }
                    else
                    {
                        for (var row = 0; row < 3; row++)
                        {
                            var value = RuleValueGenerator.Evaluate(result.EffectiveRule, attr, previewSeed, _table, row, _runId);
                            preview.Add(FormatPreview(value));
                        }
                    }
                }
            }
        }

        _messages = messages;
        _previewValues = preview;
        RebuildPreviewRows();
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(InfoBarMessage));
        OnPropertyChanged(nameof(InfoBarIsError));
        OnPropertyChanged(nameof(PreviewValues));
        OnPropertyChanged(nameof(CanSave));
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    private void RebuildPreviewRows()
    {
        PreviewRows.Clear();
        foreach (var value in _previewValues)
        {
            if (IsBlankPreview(value))
                PreviewRows.Add(new PreviewRow("— blank —", "Blank"));
            else
                PreviewRows.Add(new PreviewRow(value, "Value"));
        }
    }

    private static bool IsBlankPreview(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value is "(null)" or "(omitted)";

    private FieldRule? TryBuildDraft(AttributeMetadata attr, string op) => op switch
    {
        "constant" => TryConstantValue(attr, ConstantText, out var v) ? new ConstantRule(v) : null,
        "oneOf" => TryOneOf(attr),
        "range" => TryRange(attr),
        "pattern" => string.IsNullOrEmpty(Template) ? null : new PatternRule(Template),
        "sequence" => TrySequence(),
        "null" => new NullRule(),
        "bogus" => _restoredBogus,
        _ => null,
    };

    private FieldRule? TryOneOf(AttributeMetadata attr)
    {
        List<JsonElement> values;
        if (attr is EnumAttributeMetadata or BooleanAttributeMetadata)
        {
            values = Options.Where(o => o.IsChecked)
                .Select(o => JsonDocument.Parse(o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement)
                .ToList();
        }
        else
        {
            // ponytail: literal-list entry for non-choice scalars piggybacks on ConstantText as a
            // comma-separated list — a dedicated multi-value editor is out of scope for this task.
            if (string.IsNullOrWhiteSpace(ConstantText)) return null;
            values = [];
            foreach (var token in ConstantText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryConstantValue(attr, token, out var v)) values.Add(v);
            }
        }
        return values.Count >= 2 ? new OneOfRule(values, Pick) : null;
    }

    private FieldRule? TryRange(AttributeMetadata attr)
    {
        if (string.IsNullOrWhiteSpace(MinText) || string.IsNullOrWhiteSpace(MaxText)) return null;
        return TryConstantValue(attr, MinText, out var min) && TryConstantValue(attr, MaxText, out var max)
            ? new RangeRule(min, max)
            : null;
    }

    private FieldRule TrySequence()
    {
        _ = decimal.TryParse(StartText, out var start);
        var step = decimal.TryParse(StepText, out var s) ? s : 1;
        return new SequenceRule(start, step);
    }

    private static bool TryConstantValue(AttributeMetadata attr, string? text, out JsonElement value)
    {
        value = default;
        if (text is null) return false;
        try
        {
            var json = attr switch
            {
                StringAttributeMetadata or MemoAttributeMetadata or DateTimeAttributeMetadata
                    => JsonSerializer.Serialize(text),
                BooleanAttributeMetadata => bool.Parse(text) ? "true" : "false",
                _ => text.Trim(),
            };
            value = JsonDocument.Parse(json).RootElement;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }
    }

    private static string FormatPreview(object? value)
    {
        if (value is null) return "(null)";
        if (ReferenceEquals(value, RuleValueGenerator.Omit)) return "(omitted)";
        return value switch
        {
            OptionSetValue osv => osv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Money m => m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    // ── Picker copy (§3.2) ───────────────────────────────────────────────────

    private void ResetFromMetadata(EntityMetadata meta)
    {
        _table = meta.LogicalName ?? string.Empty;
        var attrs = meta.Attributes ?? [];
        _byName.Clear();
        foreach (var attr in attrs.Where(a => a.LogicalName is not null))
            _byName[attr.LogicalName!] = attr;

        var altKeyAttrs = (meta.Keys ?? [])
            .SelectMany(k => k.KeyAttributes ?? [])
            .ToHashSet(StringComparer.Ordinal);

        _allSettable.Clear();
        _allExcluded.Clear();
        foreach (var attr in attrs)
        {
            var column = BuildPickerColumn(attr, altKeyAttrs);
            (column.IsSelectable ? _allSettable : _allExcluded).Add(column);
        }

        RebuildFilterChips();
        OnPropertyChanged(nameof(SettableColumns));
        OnPropertyChanged(nameof(ExcludedColumns));
        OnPropertyChanged(nameof(PreviewFooterLabel));
    }

    private PickerColumn BuildPickerColumn(AttributeMetadata attr, ISet<string> altKeyAttrs)
    {
        var name = attr.LogicalName ?? string.Empty;
        var display = attr.DisplayName?.UserLocalizedLabel?.Label ?? name;
        var eligibility = RuleEligibility.Classify(attr);

        var selectable = eligibility.IsSettable && !altKeyAttrs.Contains(name);
        var disabledReason = eligibility.IsSettable && altKeyAttrs.Contains(name)
            ? "Alternate key — rejected before generation begins."
            : ReasonText(eligibility.Reason);

        var required = IsRequiredLevel(attr);
        var mapped = selectable && IsMappedName(name);
        var (stateKey, groupName) = ResolveState(selectable, mapped, required);

        return new PickerColumn(name, display, TypeLabelFor(attr), selectable, disabledReason)
        {
            StateKey = stateKey,
            GroupName = groupName,
        };
    }

    private static (string StateKey, string GroupName) ResolveState(bool selectable, bool mapped, bool required)
    {
        if (!selectable)
            return ("Disabled", "Disabled");
        if (mapped)
            return ("Mapped", "Mapped");
        if (required)
            return ("Required", "Unmapped · required");
        return ("Unmapped", "Unmapped");
    }

    private void RebuildFilterChips()
    {
        var mapped = _allSettable.Count(c => c.GroupName == "Mapped");
        var required = _allSettable.Count(IsRequired);
        var all = _allSettable.Count;
        ColumnFilterModes.Clear();
        ColumnFilterModes.Add(new ColumnFilterMode("Mapped", $"Mapped ({mapped})", mapped));
        ColumnFilterModes.Add(new ColumnFilterMode("Required", $"Required ({required})", required));
        ColumnFilterModes.Add(new ColumnFilterMode("All", $"All ({all})", all));
    }

    private void ApplyTableCounts(string tableName)
    {
        var table = _profile?.Tables.FirstOrDefault(t =>
            string.Equals(t.Table, tableName, StringComparison.OrdinalIgnoreCase));
        if (table is not null)
            _recordCount = table.Count;
        OnPropertyChanged(nameof(PreviewFooterLabel));
    }

    private bool IsRequired(PickerColumn column) =>
        _byName.TryGetValue(column.LogicalName, out var attr) && IsRequiredLevel(attr);

    private static bool IsRequiredLevel(AttributeMetadata attr) =>
        attr.RequiredLevel?.Value is AttributeRequiredLevel.SystemRequired
            or AttributeRequiredLevel.ApplicationRequired;

    private bool IsMapped(PickerColumn column) => IsMappedName(column.LogicalName);

    private bool IsMappedName(string logicalName) =>
        TryGetProfileColumns(out var cols) && cols.ContainsKey(logicalName);

    private bool TryGetProfileColumns(out Dictionary<string, FieldRule> cols)
    {
        cols = [];
        var table = _profile?.Tables.FirstOrDefault(t =>
            string.Equals(t.Table, _table, StringComparison.OrdinalIgnoreCase));
        if (table?.Columns is null)
            return false;
        cols = table.Columns;
        return true;
    }

    // Reason copy sourced verbatim from the XML doc comments on EligibilityReason
    // (src/DataGen.Core/Rules/RuleEligibility.cs) — the authoritative §3.2 wording.
    private static string? ReasonText(EligibilityReason reason) => reason switch
    {
        EligibilityReason.Settable => null,
        EligibilityReason.PlatformKey => "Primary key — assigned by the platform.",
        EligibilityReason.AutoNumber => "Auto-numbered by platform.",
        EligibilityReason.Calculated => "Platform computes this value.",
        EligibilityReason.BaseCurrency => "Derived from exchange rate.",
        EligibilityReason.StateCode => "Platform-owned state — set Status (reason) instead.",
        EligibilityReason.BpfBookkeeping => "Platform-owned state — set Status (reason) instead.",
        EligibilityReason.BinaryUpload => "File/image — needs the upload API.",
        EligibilityReason.Lookup => "Lookup — out of scope in v1.",
        EligibilityReason.NotCreatable => "Not valid for create.",
        EligibilityReason.MultiSelectV2 => "MultiSelect — rule editing planned for v2.",
        _ => reason.ToString(),
    };

    private static string TypeLabelFor(AttributeMetadata attr) => attr switch
    {
        StringAttributeMetadata => "Text",
        MemoAttributeMetadata => "Memo",
        IntegerAttributeMetadata => "Whole Number",
        BigIntAttributeMetadata => "Big Integer",
        DecimalAttributeMetadata => "Decimal",
        DoubleAttributeMetadata => "Floating Point",
        MoneyAttributeMetadata => "Money",
        DateTimeAttributeMetadata => "Date/Time",
        BooleanAttributeMetadata => "Two Options",
        MultiSelectPicklistAttributeMetadata => "MultiSelect Choice",
        StatusAttributeMetadata => "Status (reason)",
        StateAttributeMetadata => "Status",
        EnumAttributeMetadata => "Choice",
        LookupAttributeMetadata => "Lookup",
        UniqueIdentifierAttributeMetadata => "Unique Identifier",
        ImageAttributeMetadata or FileAttributeMetadata => "File/Image",
        _ => "Other",
    };

    private static IReadOnlyList<string> OpsFor(AttributeMetadata attr) => attr switch
    {
        StringAttributeMetadata or MemoAttributeMetadata => TextOps,
        IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata or MoneyAttributeMetadata => NumericOps,
        DoubleAttributeMetadata => FloatOps,
        DateTimeAttributeMetadata => DateOps,
        EnumAttributeMetadata or BooleanAttributeMetadata => ChoiceOps,
        _ => NoOps,
    };

    private IReadOnlyList<PickerColumn> Filter(List<PickerColumn> source) =>
        string.IsNullOrWhiteSpace(SearchText)
            ? source
            : source.Where(c => c.LogicalName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                              || c.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
}

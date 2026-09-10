using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.ServiceModel;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Exceptions;
using DataGen.Core.Generators;
using DataGen.Core.Metadata;
using DataGen.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.Services.Dataverse;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Profiles;
using Seedbomb.ViewModels.Controls;
using Seedbomb.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

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

/// <summary>
/// ViewModel for the rule editor dialog — the one place settable vs. platform-owned columns
/// are enumerated (S3). Bodies delegate entirely to <see cref="RuleEligibility"/>,
/// <see cref="RuleValidator"/>, and <see cref="RuleValueGenerator"/> (Wave 1, DataGen.Core.Rules);
/// this class owns no eligibility, validation, or evaluation semantics of its own.
/// </summary>
public sealed partial class RuleEditorViewModel : ObservableObject, INotifyDataErrorInfo
{
    // ── §3.1 Applies-to catalogs (Docs/field-rules-proto.html, table 3.1) ───────
    private static readonly string[] TextOps = ["constant", "oneOf", "pattern", "null"];
    private static readonly string[] NumericOps = ["constant", "oneOf", "range", "sequence", "null"]; // Whole#/BigInt/Decimal/Money
    private static readonly string[] FloatOps = ["constant", "oneOf", "range", "null"]; // Float has no sequence row
    private static readonly string[] DateOps = ["constant", "oneOf", "null"];
    private static readonly string[] ChoiceOps = ["constant", "oneOf", "null"]; // Choice/Status(reason)/Two-Options
    private static readonly string[] NoOps = [];

    private readonly Dictionary<string, AttributeMetadata> _byName;
    private readonly List<PickerColumn> _allSettable;
    private readonly List<PickerColumn> _allExcluded;
    // Settable + excluded, in metadata order — the one list ColumnsView wraps, so excluded
    // (platform-owned) columns stay visible-but-disabled instead of silently disappearing (S3).
    private readonly List<PickerColumn> _allColumns = [];
    private readonly Dictionary<string, EntityMetadata> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMetadataProvider? _metadata;
    private readonly IProfileService? _profiles;
    private readonly IAppNavigator? _navigator;
    private readonly RulesNavigationRequest? _request;
    private readonly IContentDialogService? _dialogs;
    private readonly ISnackbarService? _snackbar;
    private readonly ILogger<RuleEditorViewModel>? _logger;
    private readonly ILookupRecordPicker? _picker;
    private readonly IDataverseConnectionService? _connection;

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
    private bool _suppressBogusCascade;
    private bool _suppressDraftLoad;
    private string? _lookupDraftError;
    private int _previewGeneration;
    private int _editorGeneration;
    private int _metadataGeneration;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _pageCts;
    private CancellationTokenSource? _pickerCts;
    private CancellationTokenSource? _metadataCts;
    private SynchronizationContext? _uiContext;
    private HashSet<string> _errorProperties = [];

    /// <summary>Lookup identity editor state for the selected column.</summary>
    public LookupRuleInputViewModel LookupInput { get; }

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
        LookupInput = new LookupRuleInputViewModel();
        LookupInput.Changed += OnLookupInputChanged;
        ResetFromMetadata(meta);
        IsMetadataAvailable = true;
    }

    /// <summary>DI constructor for the Rules page. Call <see cref="LoadForProfileAsync"/> on navigate.</summary>
    [ActivatorUtilitiesConstructor]
    public RuleEditorViewModel(
        IMetadataProvider metadata,
        IProfileService profiles,
        IAppNavigator navigator,
        RulesNavigationRequest request,
        IContentDialogService? dialogs = null,
        ISnackbarService? snackbar = null,
        ILogger<RuleEditorViewModel>? logger = null,
        ILookupRecordPicker? picker = null,
        IDataverseConnectionService? connection = null)
    {
        _metadata = metadata;
        _profiles = profiles;
        _navigator = navigator;
        _request = request;
        _dialogs = dialogs;
        _snackbar = snackbar;
        _logger = logger;
        _picker = picker;
        _connection = connection;
        _byName = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);
        _allSettable = [];
        _allExcluded = [];
        _table = string.Empty;
        _recordCount = 10;
        _seed = 42;
        _runId = "rules-preview";
        LookupInput = new LookupRuleInputViewModel();
        LookupInput.Changed += OnLookupInputChanged;
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

    /// <summary>True when the selected column is a lookup or customer attribute.</summary>
    public bool IsLookupColumn =>
        SelectedColumn is not null
        && _byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
        && attr is LookupAttributeMetadata;

    /// <summary>True when constant/one-of identity editing is shown for a lookup column.</summary>
    public bool IsLookupValueOperation =>
        IsLookupColumn && SelectedOp is "constant" or "oneOf";

    /// <summary>True when the lookup-random explanation is shown.</summary>
    public bool IsLookupRandomOperation =>
        IsLookupColumn && SelectedOp == "lookupRandom";

    /// <summary>Run-time-only copy for lookupRandom preview. Interpolates the shared candidate bound.</summary>
    public string LookupRandomExplanation =>
        $"Uses up to {LookupRandomRule.MaximumCandidatesPerTarget.ToString("N0", CultureInfo.InvariantCulture)} existing records per target, captured before generation. Same seed and captured records give the same picks. Preview is resolved when the run starts.";

    /// <summary>True when a metadata load failed and the page should show reconnect/retry guidance.</summary>
    public bool HasMetadataError => !string.IsNullOrWhiteSpace(MetadataError);

    /// <summary>True when live table metadata is loaded and save/picker may run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isMetadataAvailable;

    /// <summary>True while table metadata is being fetched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isMetadataLoading;

    /// <summary>Persistent reconnect/retry copy after a metadata or connection failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetadataError))]
    private string? _metadataError;

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
        LoadDraftForColumn(value);
        // RelayCommand doesn't auto-hook CommandManager.RequerySuggested — without this, a button
        // bound to one of these commands (Cancel; Delete rule's mapped-state check) can bind while
        // SelectedColumn is still null/unmapped and never re-query CanExecute again.
        CancelRuleCommand.NotifyCanExecuteChanged();
        DeleteRuleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Loads <paramref name="column"/>'s committed rule (or a blank default, if it has none) into
    /// the editor fields. Used both when the picker selection changes and by
    /// <see cref="CancelRule"/> to discard an in-progress edit back to last-committed state.
    /// </summary>
    private void LoadDraftForColumn(PickerColumn? column)
    {
        CancelPicker();
        _suppressDraftLoad = true;
        try
        {
            Options.Clear();
            AttributeMetadata? attr = null;
            if (column is not null)
                _byName.TryGetValue(column.LogicalName, out attr);

            LookupInput.Configure(attr as LookupAttributeMetadata);
            Pick = OneOfPick.Random;

            if (attr is EnumAttributeMetadata em)
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
            NotifyLookupPresentation();
            SelectedOp = AvailableOps.FirstOrDefault() ?? string.Empty;
            if (column is not null
                && TryGetProfileColumns(out var cols)
                && cols.TryGetValue(column.LogicalName, out var existing))
            {
                ApplyExistingRule(existing);
            }
        }
        finally
        {
            _suppressDraftLoad = false;
        }

        Revalidate();
    }

    private bool CanCancelRule() => SelectedColumn is not null;

    /// <summary>Discards any unsaved edit to the current rule, reverting to last-committed state.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelRule))]
    private void CancelRule() => LoadDraftForColumn(SelectedColumn);

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
        CancelPicker();
        OnPropertyChanged(nameof(SelectedOperation));
        OnPropertyChanged(nameof(IsTemplateOperation));
        NotifyLookupPresentation();
        if (_suppressDraftLoad)
            return;
        if (value == "bogus")
            RefreshBogusCatalogLists();
        else if (!_suppressBogusCascade)
            ClearBogusEditor();
        Revalidate();
    }

    [ObservableProperty] private IReadOnlyList<string> _bogusApis = [];
    [ObservableProperty] private string? _selectedBogusApi;
    [ObservableProperty] private IReadOnlyList<BogusEndpointOption> _bogusEndpoints = [];
    [ObservableProperty] private string? _selectedBogusEndpoint;
    [ObservableProperty] private bool _bogusHasNumericArgs;
    [ObservableProperty] private bool _bogusHasLengthArg;
    [ObservableProperty] private bool _bogusHasDateArgs;
    [ObservableProperty] private string _bogusMinNumber = "";
    [ObservableProperty] private string _bogusMaxNumber = "";
    [ObservableProperty] private string _bogusLengthText = "";
    [ObservableProperty] private DateTime? _bogusMinDate;
    [ObservableProperty] private DateTime? _bogusMaxDate;

    /// <inheritdoc />
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    bool INotifyDataErrorInfo.HasErrors => _messages.Any(m => m.Severity == RuleMessageSeverity.Error);

    /// <inheritdoc />
    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return _messages.Select(m => m.Text).ToList();

        return _messages
            .Where(m => PropertyNameFor(m.Target) == propertyName)
            .Select(m => m.Text)
            .ToList();
    }

    partial void OnSelectedBogusApiChanged(string? value)
    {
        BogusEndpoints = value is null || !TryTargetKind(out var kind)
            ? []
            : BogusCatalogQuery.EndpointsFor(value, kind);
        if (_suppressBogusCascade)
            return;
        SelectedBogusEndpoint = null;
        ClearAllBogusArguments();
        Revalidate();
        NotifyCanSaveChanged();
    }

    partial void OnSelectedBogusEndpointChanged(string? value)
    {
        if (_suppressBogusCascade)
            return;
        ApplyArgumentVisibility(value);
        if (value is null)
            ClearAllBogusArguments();
        Revalidate();
        NotifyCanSaveChanged();
    }

    partial void OnBogusMinNumberChanged(string value) => Revalidate();
    partial void OnBogusMaxNumberChanged(string value) => Revalidate();
    partial void OnBogusLengthTextChanged(string value) => Revalidate();
    partial void OnBogusMinDateChanged(DateTime? value) => Revalidate();
    partial void OnBogusMaxDateChanged(DateTime? value) => Revalidate();

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
    public bool CanSave =>
        IsMetadataAvailable
        && !IsMetadataLoading
        && _effectiveRule is not null
        && !_messages.Any(m => m.Severity == RuleMessageSeverity.Error);

    /// <summary>Returns the last-validated effective rule (clamped range, etc.), or null if nothing valid is drafted.</summary>
    public FieldRule? BuildRule() => _effectiveRule;

    /// <summary>
    /// Loads an existing board rule into the editor after <see cref="SelectedColumn"/> is set.
    /// Restores op + typed parameters so edit mode does not always fall back to the default op.
    /// </summary>
    public void ApplyExistingRule(FieldRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (SelectedColumn is not null
            && _byName.TryGetValue(SelectedColumn.LogicalName, out var lookupAttr)
            && lookupAttr is LookupAttributeMetadata)
        {
            var previousSuppress = _suppressDraftLoad;
            _suppressDraftLoad = true;
            try
            {
                LookupInput.Restore(rule);
                switch (rule)
                {
                    case ConstantRule:
                        SelectedOp = "constant";
                        break;
                    case OneOfRule o:
                        SelectedOp = "oneOf";
                        Pick = o.Pick;
                        break;
                    case NullRule:
                        SelectedOp = "null";
                        break;
                    case LookupRandomRule:
                        SelectedOp = "lookupRandom";
                        break;
                }
            }
            finally
            {
                _suppressDraftLoad = previousSuppress;
            }

            if (!previousSuppress)
                Revalidate();
            return;
        }

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
                RestoreBogus(b);
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
            // Disabled (platform-owned) columns never belong to Required/Mapped — they're
            // never actionable, so they'd only mislead the checklist those chips exist for.
            "Required" => column.IsSelectable && IsRequired(column),
            // Metadata ctor has no profile: keep settable columns visible so required-unmapped
            // (and the rest of the picker) stay testable without a ColumnsView.
            "Mapped" => column.IsSelectable && (IsMapped(column) || IsRequired(column) || _profile is null),
            "Disabled" => !column.IsSelectable,
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
            _allColumns.Clear();
            OnPropertyChanged(nameof(SettableColumns));
            OnPropertyChanged(nameof(ExcludedColumns));
            ColumnFilterModes.Clear();
            ColumnsView = null;
            OnPropertyChanged(nameof(ColumnsView));
            IsMetadataAvailable = false;
            MetadataError = "No profile is loaded. Open a profile, then retry.";
            NotifyCanSaveChanged();
            RetryMetadataCommand.NotifyCanExecuteChanged();
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

        if (SelectedTable is not null)
            ApplyTableCounts(SelectedTable.LogicalName);

        await ReloadMetadataAsync(ct);
    }

    private bool CanRetryMetadata() =>
        _profile is not null && _metadata is not null && !IsMetadataLoading;

    /// <summary>Retries metadata load using the retained profile, save callback, and selected table.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryMetadata))]
    private Task RetryMetadataAsync(CancellationToken ct) => ReloadMetadataAsync(ct);

    private async Task ReloadMetadataAsync(CancellationToken ct)
    {
        if (_profile is null || _metadata is null)
        {
            IsMetadataAvailable = false;
            IsMetadataLoading = false;
            MetadataError = "No profile is loaded. Open a profile, then retry.";
            NotifyCanSaveChanged();
            RetryMetadataCommand.NotifyCanExecuteChanged();
            PickLookupRecordsCommand.NotifyCanExecuteChanged();
            return;
        }

        if (SelectedTable is null)
        {
            IsMetadataAvailable = false;
            IsMetadataLoading = false;
            MetadataError = "No table is selected. Choose a table, then retry.";
            NotifyCanSaveChanged();
            RetryMetadataCommand.NotifyCanExecuteChanged();
            return;
        }

        var generation = Interlocked.Increment(ref _metadataGeneration);
        _metadataCts?.Cancel();
        _metadataCts?.Dispose();
        _metadataCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts?.Token ?? CancellationToken.None, ct);
        var token = _metadataCts.Token;

        IsMetadataLoading = true;
        MetadataError = null;
        NotifyCanSaveChanged();
        RetryMetadataCommand.NotifyCanExecuteChanged();
        PickLookupRecordsCommand.NotifyCanExecuteChanged();

        try
        {
            token.ThrowIfCancellationRequested();
            var names = Tables.Select(t => t.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            IReadOnlyList<EntityMetadata> list;
            try
            {
                list = await _metadata.GetEntitiesAsync(names, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (generation != _metadataGeneration)
                    return;
                FailMetadata(DescribeMetadataFailure(ex), ex);
                return;
            }

            if (generation != _metadataGeneration || token.IsCancellationRequested)
                return;

            _entities.Clear();
            foreach (var entity in list)
            {
                if (entity.LogicalName is not null)
                    _entities[entity.LogicalName] = entity;
            }

            if (!_entities.TryGetValue(SelectedTable.LogicalName, out var meta))
            {
                if (generation != _metadataGeneration)
                    return;
                IsMetadataAvailable = false;
                MetadataError = "Table metadata was not returned. Reconnect and retry.";
                NotifyCanSaveChanged();
                return;
            }

            var selectedName = SelectedColumn?.LogicalName;
            ResetFromMetadata(meta);

            ColumnsView = CollectionViewSource.GetDefaultView(_allColumns);
            ColumnsView.Filter = o => o is PickerColumn c && MatchesColumnFilter(c);
            if (ColumnsView is CollectionView view)
            {
                using (view.DeferRefresh())
                {
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new SortDescription(nameof(PickerColumn.GroupOrder), ListSortDirection.Ascending));
                    view.GroupDescriptions.Clear();
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerColumn.GroupName)));
                }
            }

            OnPropertyChanged(nameof(ColumnsView));
            IsMetadataAvailable = true;
            MetadataError = null;
            if (selectedName is not null)
            {
                SelectedColumn = _allColumns.FirstOrDefault(c =>
                    string.Equals(c.LogicalName, selectedName, StringComparison.OrdinalIgnoreCase));
            }

            NotifyCanSaveChanged();
            RetryMetadataCommand.NotifyCanExecuteChanged();
            PickLookupRecordsCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            // superseded or page closed — a newer generation owns UI state
        }
        finally
        {
            if (generation == _metadataGeneration)
            {
                IsMetadataLoading = false;
                NotifyCanSaveChanged();
                RetryMetadataCommand.NotifyCanExecuteChanged();
                PickLookupRecordsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    partial void OnSelectedTableChanged(RuleTableOption? value)
    {
        CancelPicker();
        if (_suppressTableChange || value is null)
            return;

        ApplyTableCounts(value.LogicalName);
        if (!_entities.TryGetValue(value.LogicalName, out var meta))
            return;

        SelectedColumn = null;
        ResetFromMetadata(meta);
        if (ColumnsView is CollectionView grouped)
        {
            using (grouped.DeferRefresh())
            {
                grouped.SortDescriptions.Clear();
                grouped.SortDescriptions.Add(new SortDescription(nameof(PickerColumn.GroupOrder), ListSortDirection.Ascending));
                grouped.GroupDescriptions.Clear();
                grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerColumn.GroupName)));
            }
        }

        ColumnsView?.Refresh();
        OnPropertyChanged(nameof(PreviewFooterLabel));
        SelectedColumn = _allSettable.FirstOrDefault();
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
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(nameof(PickerColumn.GroupOrder), ListSortDirection.Ascending));
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

    /// <summary>SaveRuleCommand shares CanSaveProfile's predicate, so it must be invalidated
    /// everywhere SaveProfileCommand is, or the Preview pane's Save button desyncs from the
    /// header's.</summary>
    private void NotifyCanSaveChanged()
    {
        SaveProfileCommand.NotifyCanExecuteChanged();
        SaveRuleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Builds and commits the current draft rule for <see cref="SelectedColumn"/> into the
    /// profile (and disk, if it's already a saved profile), without leaving the page.</summary>
    /// <returns>False if there was no valid draft to commit.</returns>
    private async Task<bool> CommitSelectedColumnRuleAsync(CancellationToken ct)
    {
        if (_profiles is null || _profile is null || SelectedColumn is null)
            return false;

        var rule = BuildRule();
        if (rule is null)
            return false;

        var tables = _profile.Tables.ToList();
        var idx = tables.FindIndex(t => string.Equals(t.Table, _table, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;

        var cols = tables[idx].Columns ?? new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase);
        cols[SelectedColumn.LogicalName] = rule;
        tables[idx] = tables[idx] with { Columns = cols };
        _profile = _profile with { Tables = tables };

        // Generate working-set snapshots are not in the store — callback only, no disk write.
        try
        {
            var names = await _profiles.ListAsync(ct);
            if (names.Any(n => string.Equals(n, _profile.Name, StringComparison.OrdinalIgnoreCase)))
                await _profiles.SaveAsync(_profile, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailProfileStore("save", ex);
            return false;
        }

        _onSaved?.Invoke(_profile);

        if (_entities.TryGetValue(_table, out var meta))
        {
            // ResetFromMetadata rebuilds every PickerColumn record; re-point SelectedColumn at its
            // fresh instance by name or the ListBox loses its highlight when the mapped state flips.
            var name = SelectedColumn.LogicalName;
            ResetFromMetadata(meta);
            ColumnsView?.Refresh();
            SelectedColumn = _allColumns.FirstOrDefault(c =>
                string.Equals(c.LogicalName, name, StringComparison.OrdinalIgnoreCase)) ?? SelectedColumn;
        }

        return true;
    }

    /// <summary>Commits the current rule without leaving the page (Preview pane's Save button).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveRuleAsync(CancellationToken ct) => await CommitSelectedColumnRuleAsync(ct);

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync(CancellationToken ct)
    {
        if (!await CommitSelectedColumnRuleAsync(ct))
            return;

        if (_returnPage is not null)
            _navigator?.Navigate(_returnPage);
    }

    private bool CanDeleteRule() =>
        _profiles is not null && _profile is not null && SelectedColumn is not null && IsMapped(SelectedColumn);

    /// <summary>Test seam for <see cref="ConfirmDeleteRuleAsync"/> — bypasses the real dialog.</summary>
    internal Func<string, Task<bool>>? ConfirmDeleteRule { get; set; }

    private async Task<bool> ConfirmDeleteRuleAsync(string columnDisplayName)
    {
        if (ConfirmDeleteRule is not null)
            return await ConfirmDeleteRule(columnDisplayName);
        if (_dialogs is null)
            return false;

        var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Delete rule",
            Content = $"Delete the rule for '{columnDisplayName}'? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        });
        return result == ContentDialogResult.Primary;
    }

    /// <summary>Removes the current column's rule entirely (reverts it to Unmapped), after confirm.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteRule))]
    private async Task DeleteRuleAsync(CancellationToken ct)
    {
        if (_profiles is null || _profile is null || SelectedColumn is null)
            return;
        if (!await ConfirmDeleteRuleAsync(SelectedColumn.DisplayName))
            return;

        var tables = _profile.Tables.ToList();
        var idx = tables.FindIndex(t => string.Equals(t.Table, _table, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return;

        var cols = tables[idx].Columns ?? new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase);
        cols.Remove(SelectedColumn.LogicalName);
        tables[idx] = tables[idx] with { Columns = cols };
        _profile = _profile with { Tables = tables };

        try
        {
            var names = await _profiles.ListAsync(ct);
            if (names.Any(n => string.Equals(n, _profile.Name, StringComparison.OrdinalIgnoreCase)))
                await _profiles.SaveAsync(_profile, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailProfileStore("delete", ex);
            return;
        }

        _onSaved?.Invoke(_profile);

        if (_entities.TryGetValue(_table, out var meta))
        {
            var name = SelectedColumn.LogicalName;
            ResetFromMetadata(meta);
            ColumnsView?.Refresh();
            SelectedColumn = _allColumns.FirstOrDefault(c =>
                string.Equals(c.LogicalName, name, StringComparison.OrdinalIgnoreCase)) ?? SelectedColumn;
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
        "bogus" => new("bogus", "bogus", "generated by Bogus"),
        "lookupRandom" => new(
            "lookupRandom",
            "random (existing records)",
            $"up to {LookupRandomRule.MaximumCandidatesPerTarget.ToString("N0", CultureInfo.InvariantCulture)} existing records per target"),
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
            if (SelectedOp == "bogus"
                && (string.IsNullOrEmpty(SelectedBogusApi) || string.IsNullOrEmpty(SelectedBogusEndpoint)))
            {
                messages.Add(new RuleMessage(
                    RuleMessageSeverity.Error,
                    "Select a Bogus endpoint.",
                    RuleMessageCode.UnknownEndpoint,
                    RuleInputTarget.Endpoint));
            }
            else
            {
                var draft = TryBuildDraft(attr, SelectedOp);
                if (draft is null)
                {
                    messages.Add(new RuleMessage(
                        RuleMessageSeverity.Error,
                        _lookupDraftError ?? "Enter a value for this rule."));
                }
                else
                {
                    var result = RuleValidator.Validate(
                        draft, attr, new RuleValidationContext(_table, _recordCount, _runId));
                    messages = result.Messages.ToList();
                    if (result.IsValid && result.EffectiveRule is not null)
                        _effectiveRule = result.EffectiveRule;
                }
            }
        }

        _messages = messages.OrderBy(m => m.Severity == RuleMessageSeverity.Error ? 0 : 1).ToList();
        PublishErrors();
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(InfoBarMessage));
        OnPropertyChanged(nameof(InfoBarIsError));
        OnPropertyChanged(nameof(CanSave));
        NotifyCanSaveChanged();
        SchedulePreview();
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

    private FieldRule? TryBuildDraft(AttributeMetadata attr, string op)
    {
        if (attr is LookupAttributeMetadata)
            return LookupInput.Build(op, Pick, out _lookupDraftError);

        _lookupDraftError = null;
        return op switch
        {
            "constant" => TryConstantValue(attr, ConstantText, out var v) ? new ConstantRule(v) : null,
            "oneOf" => TryOneOf(attr),
            "range" => TryRange(attr),
            "pattern" => string.IsNullOrEmpty(Template) ? null : new PatternRule(Template),
            "sequence" => TrySequence(),
            "null" => new NullRule(),
            "bogus" => TryBuildBogus(),
            _ => null,
        };
    }

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
            EntityReference r => $"{r.LogicalName} · {r.Id:D}",
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
        _allColumns.Clear();
        foreach (var attr in attrs)
        {
            var column = BuildPickerColumn(attr, altKeyAttrs);
            (column.IsSelectable ? _allSettable : _allExcluded).Add(column);
            _allColumns.Add(column);
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
        var (stateKey, groupName, groupOrder) = ResolveState(selectable, mapped, required);

        return new PickerColumn(name, display, TypeLabelFor(attr), selectable, disabledReason)
        {
            StateKey = stateKey,
            GroupName = groupName,
            GroupOrder = groupOrder,
        };
    }

    private static (string StateKey, string GroupName, int GroupOrder) ResolveState(bool selectable, bool mapped, bool required)
    {
        if (!selectable)
            return ("Disabled", "Disabled", 3);
        if (mapped)
            return ("Mapped", "Mapped", 0);
        if (required)
            return ("Required", "Unmapped · required", 1);
        return ("Unmapped", "Unmapped", 2);
    }

    private void RebuildFilterChips()
    {
        var mapped = _allSettable.Count(c => c.GroupName == "Mapped");
        var required = _allSettable.Count(IsRequired);
        var disabled = _allExcluded.Count;
        // "All" now includes the Disabled group shown alongside them in ColumnsView (S3).
        var all = _allColumns.Count;
        ColumnFilterModes.Clear();
        ColumnFilterModes.Add(new ColumnFilterMode("Mapped", $"Mapped ({mapped})", mapped));
        ColumnFilterModes.Add(new ColumnFilterMode("Required", $"Required ({required})", required));
        ColumnFilterModes.Add(new ColumnFilterMode("Disabled", $"Disabled ({disabled})", disabled));
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
        EligibilityReason.Lookup => "Unsupported lookup type or target metadata.",
        EligibilityReason.OwnerAssigned => "Owner is assigned by Dataverse; owner rules are not supported.",
        EligibilityReason.PolymorphicType => "Owner/Customer type — determined by its paired lookup value.",
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
        LookupAttributeMetadata lookup =>
            lookup.AttributeType == AttributeTypeCode.Customer ? "customer" : "lookup",
        UniqueIdentifierAttributeMetadata => "Unique Identifier",
        ImageAttributeMetadata or FileAttributeMetadata => "File/Image",
        _ => "Other",
    };

    private static IReadOnlyList<string> OpsFor(AttributeMetadata attr)
    {
        if (attr is LookupAttributeMetadata)
            return RuleEligibility.Classify(attr).IsSettable
                ? ["constant", "oneOf", "lookupRandom", "null"]
                : NoOps;

        var ops = attr switch
        {
            StringAttributeMetadata or MemoAttributeMetadata => TextOps,
            IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata or MoneyAttributeMetadata => NumericOps,
            DoubleAttributeMetadata => FloatOps,
            DateTimeAttributeMetadata => DateOps,
            BooleanAttributeMetadata => ChoiceOps,
            EnumAttributeMetadata => ChoiceOps,
            _ => NoOps,
        };

        if (attr is EnumAttributeMetadata)
            return ops;
        if (TryMapKind(attr, out var kind) && BogusCatalogQuery.HasAny(kind))
            return [.. ops, "bogus"];
        return ops;
    }

    private void RefreshBogusCatalogLists()
    {
        if (!TryTargetKind(out var kind))
        {
            BogusApis = [];
            BogusEndpoints = [];
            return;
        }

        BogusApis = BogusCatalogQuery.ApisFor(kind);
        BogusEndpoints = SelectedBogusApi is null
            ? []
            : BogusCatalogQuery.EndpointsFor(SelectedBogusApi, kind);
    }

    private bool TryTargetKind(out DataverseValueKind kind)
    {
        kind = default;
        return SelectedColumn is not null
            && _byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
            && TryMapKind(attr, out kind);
    }

    private static bool TryMapKind(AttributeMetadata attr, out DataverseValueKind kind)
    {
        switch (attr)
        {
            case StringAttributeMetadata:
                kind = DataverseValueKind.String;
                return true;
            case MemoAttributeMetadata:
                kind = DataverseValueKind.Memo;
                return true;
            case BooleanAttributeMetadata:
                kind = DataverseValueKind.Boolean;
                return true;
            case IntegerAttributeMetadata:
                kind = DataverseValueKind.Integer;
                return true;
            case BigIntAttributeMetadata:
                kind = DataverseValueKind.BigInt;
                return true;
            case DecimalAttributeMetadata:
                kind = DataverseValueKind.Decimal;
                return true;
            case DoubleAttributeMetadata:
                kind = DataverseValueKind.Double;
                return true;
            case MoneyAttributeMetadata:
                kind = DataverseValueKind.Money;
                return true;
            case DateTimeAttributeMetadata:
                kind = DataverseValueKind.DateTime;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private void RestoreBogus(BogusRule rule)
    {
        _suppressBogusCascade = true;
        try
        {
            SelectedOp = "bogus";
            RefreshBogusCatalogLists();
            SelectedBogusApi = rule.Api;
            if (TryTargetKind(out var kind))
                BogusEndpoints = BogusCatalogQuery.EndpointsFor(rule.Api, kind);
            SelectedBogusEndpoint = $"{rule.Api}.{rule.Endpoint}";
            ApplyArgumentVisibility(SelectedBogusEndpoint);
            RestoreBogusArguments(rule);
        }
        finally
        {
            _suppressBogusCascade = false;
        }

        Revalidate();
    }

    private void RestoreBogusArguments(BogusRule rule)
    {
        ClearAllBogusArguments();
        if (rule.Args.TryGetValue("min", out var min))
        {
            if (BogusHasDateArgs && DateOnly.TryParse(min.GetString(), out var minDate))
                BogusMinDate = minDate.ToDateTime(TimeOnly.MinValue);
            else
                BogusMinNumber = JsonElementToEditorText(min);
        }

        if (rule.Args.TryGetValue("max", out var max))
        {
            if (BogusHasDateArgs && DateOnly.TryParse(max.GetString(), out var maxDate))
                BogusMaxDate = maxDate.ToDateTime(TimeOnly.MinValue);
            else
                BogusMaxNumber = JsonElementToEditorText(max);
        }

        if (rule.Args.TryGetValue("length", out var length))
            BogusLengthText = JsonElementToEditorText(length);
    }

    private FieldRule? TryBuildBogus()
    {
        if (string.IsNullOrEmpty(SelectedBogusApi) || string.IsNullOrEmpty(SelectedBogusEndpoint))
            return null;

        var dot = SelectedBogusEndpoint.LastIndexOf('.');
        var endpoint = dot >= 0 ? SelectedBogusEndpoint[(dot + 1)..] : SelectedBogusEndpoint;
        Dictionary<string, JsonElement>? args = null;
        if (BogusHasNumericArgs)
        {
            args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(BogusMinNumber)
                && TryParseJsonNumber(BogusMinNumber, out var min))
                args["min"] = min;
            if (!string.IsNullOrWhiteSpace(BogusMaxNumber)
                && TryParseJsonNumber(BogusMaxNumber, out var max))
                args["max"] = max;
            if (args.Count == 0)
                args = null;
        }
        else if (BogusHasLengthArg && !string.IsNullOrWhiteSpace(BogusLengthText)
                 && TryParseJsonNumber(BogusLengthText, out var length))
        {
            args = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["length"] = length };
        }
        else if (BogusHasDateArgs && BogusMinDate is { } minDate && BogusMaxDate is { } maxDate)
        {
            args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["min"] = JsonSerializer.SerializeToElement(DateOnly.FromDateTime(minDate).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                ["max"] = JsonSerializer.SerializeToElement(DateOnly.FromDateTime(maxDate).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            };
        }

        return new BogusRule(SelectedBogusApi, endpoint, 1, args);
    }

    private static bool TryParseJsonNumber(string text, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Number)
            {
                element = doc.RootElement.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
            // Malformed text is handed to the validator via a string element.
        }

        element = JsonSerializer.SerializeToElement(text);
        return true;
    }

    private void ApplyArgumentVisibility(string? endpointId)
    {
        var kind = BogusUiArgumentKind.None;
        if (SelectedBogusApi is not null && endpointId is not null)
        {
            var dot = endpointId.LastIndexOf('.');
            var endpoint = dot >= 0 ? endpointId[(dot + 1)..] : endpointId;
            kind = BogusCatalogQuery.ArgumentKind(SelectedBogusApi, endpoint);
        }

        BogusHasNumericArgs = kind == BogusUiArgumentKind.NumericRange;
        BogusHasLengthArg = kind == BogusUiArgumentKind.Length;
        BogusHasDateArgs = kind == BogusUiArgumentKind.DateRange;
    }

    private void ClearBogusEditor()
    {
        _suppressBogusCascade = true;
        try
        {
            SelectedBogusApi = null;
            SelectedBogusEndpoint = null;
            BogusApis = [];
            BogusEndpoints = [];
            ClearAllBogusArguments();
            ApplyArgumentVisibility(null);
        }
        finally
        {
            _suppressBogusCascade = false;
        }
    }

    private void ClearAllBogusArguments()
    {
        BogusMinNumber = "";
        BogusMaxNumber = "";
        BogusLengthText = "";
        BogusMinDate = null;
        BogusMaxDate = null;
    }

    private void SchedulePreview()
    {
        var generation = Interlocked.Increment(ref _previewGeneration);
        var effective = _effectiveRule;
        var column = SelectedColumn;
        if (effective is null
            || column is null
            || !_byName.TryGetValue(column.LogicalName, out var attr))
        {
            if (generation == _previewGeneration)
                PublishPreview([]);
            return;
        }

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;
        var seed = _seed + _previewSalt;
        var table = _table;
        var runId = _runId;
        var count = _recordCount;
        _ = RunPreviewAsync(generation, effective, attr, seed, table, runId, count, ct);
    }

    private async Task RunPreviewAsync(
        int generation,
        FieldRule effective,
        AttributeMetadata attr,
        int seed,
        string table,
        string runId,
        int recordCount,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(150, ct);
            if (generation != _previewGeneration)
                return;

            var preview = new List<string>(3);
            var eval = new RuleEvaluationContext(table, seed, DeterministicFaker.DefaultLocale, runId, recordCount);
            if (effective is LookupRandomRule)
            {
                preview.Add(LookupRandomExplanation);
            }
            else if (effective is BogusRule bogus)
            {
                var prepared = BogusRulePreparer.CompileRule(bogus, attr, eval);
                using var session = new BogusEvaluatorSession(eval.Locale);
                for (var row = 0; row < 3; row++)
                    preview.Add(FormatPreview(session.Evaluate(prepared, attr, eval, row)));
            }
            else
            {
                for (var row = 0; row < 3; row++)
                    preview.Add(FormatPreview(RuleValueGenerator.Evaluate(effective, attr, seed, table, row, runId)));
            }

            if (generation != _previewGeneration)
                return;
            PublishPreview(preview);
        }
        catch (OperationCanceledException)
        {
            // superseded edit
        }
        catch (InvalidOperationException)
        {
            if (generation == _previewGeneration)
                PublishPreview([]);
        }
    }

    private void PublishPreview(IReadOnlyList<string> preview)
    {
        _previewValues = preview;
        RebuildPreviewRows();
        OnPropertyChanged(nameof(PreviewValues));
    }

    private void PublishErrors()
    {
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in _messages)
        {
            var name = PropertyNameFor(message.Target);
            if (name is not null)
                next.Add(name);
        }

        var union = new HashSet<string>(_errorProperties, StringComparer.Ordinal);
        union.UnionWith(next);
        _errorProperties = next;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(null));
        foreach (var name in union)
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(name));
    }

    private static string? PropertyNameFor(RuleInputTarget target) => target switch
    {
        RuleInputTarget.Rule => nameof(SelectedOp),
        RuleInputTarget.Api => nameof(SelectedBogusApi),
        RuleInputTarget.Endpoint => nameof(SelectedBogusEndpoint),
        RuleInputTarget.Minimum => nameof(BogusMinNumber),
        RuleInputTarget.Maximum => nameof(BogusMaxNumber),
        RuleInputTarget.Length => nameof(BogusLengthText),
        RuleInputTarget.MinimumDate => nameof(BogusMinDate),
        RuleInputTarget.MaximumDate => nameof(BogusMaxDate),
        _ => nameof(SelectedOp),
    };

    private IReadOnlyList<PickerColumn> Filter(List<PickerColumn> source) =>
        string.IsNullOrWhiteSpace(SearchText)
            ? source
            : source.Where(c => c.LogicalName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                              || c.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();

    /// <summary>Starts page lifetime: captures the UI context and listens for connection resets.</summary>
    public void Activate()
    {
        _uiContext = SynchronizationContext.Current;
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = new CancellationTokenSource();
        if (_connection is not null)
        {
            _connection.ConnectionReset -= OnConnectionReset;
            _connection.ConnectionReset += OnConnectionReset;
        }
    }

    /// <summary>Ends page lifetime and cancels picker, preview, and metadata work.</summary>
    public void Deactivate()
    {
        if (_connection is not null)
            _connection.ConnectionReset -= OnConnectionReset;
        Interlocked.Increment(ref _editorGeneration);
        Interlocked.Increment(ref _previewGeneration);
        Interlocked.Increment(ref _metadataGeneration);
        _pageCts?.Cancel();
        _pickerCts?.Cancel();
        _previewCts?.Cancel();
        _metadataCts?.Cancel();
    }

    private bool CanPickLookupRecords() =>
        _picker is not null
        && IsMetadataAvailable
        && !IsMetadataLoading
        && IsLookupValueOperation
        && SelectedColumn is not null;

    /// <summary>Opens the lookup record picker and replaces selection only after Add on the current column.</summary>
    [RelayCommand(CanExecute = nameof(CanPickLookupRecords))]
    private async Task PickLookupRecordsAsync(CancellationToken ct)
    {
        if (_picker is null
            || SelectedColumn is null
            || !_byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
            || attr is not LookupAttributeMetadata lookup)
            return;

        var table = _table;
        var column = SelectedColumn.LogicalName;
        var op = SelectedOp;
        var single = SelectedOp == "constant";

        CancelPicker();
        var generation = _editorGeneration;
        _pickerCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts?.Token ?? CancellationToken.None, ct);
        var token = _pickerCts.Token;

        IReadOnlyList<LookupRuleValue>? result;
        try
        {
            result = await _picker.PickAsync(lookup, LookupInput.Records.ToArray(), single, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (result is null
            || token.IsCancellationRequested
            || generation != _editorGeneration
            || !string.Equals(table, _table, StringComparison.OrdinalIgnoreCase)
            || SelectedColumn is null
            || !string.Equals(column, SelectedColumn.LogicalName, StringComparison.OrdinalIgnoreCase)
            || SelectedOp != op)
            return;

        if (!_byName.TryGetValue(column, out var currentAttr) || currentAttr is not LookupAttributeMetadata currentLookup)
            return;

        var accepted = new List<LookupRuleValue>(result.Count);
        foreach (var value in result)
        {
            if (!LookupRuleValue.TryParse(value.ToJson(), currentLookup, out var parsed, out _) || parsed is null)
                return;
            accepted.Add(parsed);
        }

        LookupInput.ReplaceSelection(accepted);
    }

    private void OnLookupInputChanged(object? sender, EventArgs e)
    {
        if (!_suppressDraftLoad)
            Revalidate();
    }

    private void NotifyLookupPresentation()
    {
        OnPropertyChanged(nameof(IsLookupColumn));
        OnPropertyChanged(nameof(IsLookupValueOperation));
        OnPropertyChanged(nameof(IsLookupRandomOperation));
        PickLookupRecordsCommand.NotifyCanExecuteChanged();
    }

    private void CancelPicker()
    {
        Interlocked.Increment(ref _editorGeneration);
        _pickerCts?.Cancel();
        _pickerCts?.Dispose();
        _pickerCts = null;
    }

    private void OnConnectionReset(object? sender, EventArgs e)
    {
        InvalidateInFlightWork();
        if (_uiContext is not null)
            _uiContext.Post(_ => ApplyConnectionResetUi(), null);
        else
            ApplyConnectionResetUi();
    }

    private void InvalidateInFlightWork()
    {
        Interlocked.Increment(ref _editorGeneration);
        Interlocked.Increment(ref _previewGeneration);
        Interlocked.Increment(ref _metadataGeneration);
        _pickerCts?.Cancel();
        _previewCts?.Cancel();
        _metadataCts?.Cancel();
    }

    private void ApplyConnectionResetUi()
    {
        _entities.Clear();
        IsMetadataAvailable = false;
        IsMetadataLoading = false;
        MetadataError = "Connection changed. Reconnect and retry to reload table metadata.";
        PublishPreview([]);
        NotifyCanSaveChanged();
        RetryMetadataCommand.NotifyCanExecuteChanged();
        PickLookupRecordsCommand.NotifyCanExecuteChanged();
    }

    private void FailMetadata(string message, Exception ex)
    {
        _logger?.LogError(ex, "Failed to load table metadata for the Rules page");
        _snackbar?.Show("Couldn't load table metadata", message,
            ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
        IsMetadataAvailable = false;
        MetadataError = string.IsNullOrWhiteSpace(message)
            ? "Couldn't load table metadata. Reconnect and retry."
            : $"{message} Reconnect and retry.";
        NotifyCanSaveChanged();
        RetryMetadataCommand.NotifyCanExecuteChanged();
        PickLookupRecordsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Routes a profile-store failure to the same banner + snackbar surface as <see cref="FailMetadata"/>.</summary>
    private void FailProfileStore(string action, Exception ex)
    {
        _logger?.LogError(ex, "Failed to {Action} the rule profile", action);
        _snackbar?.Show($"Couldn't {action} the rule", ex.Message,
            ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        MetadataError = ex.Message;
    }

    private static string DescribeMetadataFailure(Exception ex) => ex switch
    {
        SchemaException schema => schema.Message,
        InvalidOperationException invalid => invalid.Message,
        FaultException<OrganizationServiceFault> fault =>
            $"Dataverse error {fault.Detail.ErrorCode}: {fault.Detail.Message}",
        FaultException fault => fault.Message,
        _ => ex.Message,
    };
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeedBomb.Core.Metadata;
using SeedBomb.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using SeedBomb.Services.Dataverse;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.ViewModels.Controls;
using SeedBomb.Views.Pages;
using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace SeedBomb.ViewModels;

/// <summary>
/// ViewModel for the rule editor dialog — the one place settable vs. platform-owned columns
/// are enumerated (S3). Bodies delegate entirely to <see cref="RuleEligibility"/>,
/// <see cref="RuleValidator"/>, and <see cref="RuleValueGenerator"/> (Wave 1, SeedBomb.Core.Rules);
/// this class owns no eligibility, validation, or evaluation semantics of its own.
/// </summary>
public sealed partial class RuleEditorViewModel : ObservableObject, INotifyDataErrorInfo
{
    // ── §3.1 Applies-to catalogs (Docs/field-rules-proto.html, table 3.1) ───────
    private static readonly string[] TextOps = ["constant", "oneOf", "pattern", "null"];

    private static readonly string[]
        NumericOps = ["constant", "oneOf", "range", "sequence", "null"]; // Whole#/BigInt/Decimal/Money

    private static readonly string[] FloatOps = ["constant", "oneOf", "range", "null"]; // Float has no sequence row
    private static readonly string[] DateOps = ["constant", "oneOf", "null"];
    private static readonly string[] ChoiceOps = ["constant", "oneOf", "null"]; // Choice/Status(reason)/Two-Options
    private static readonly string[] NoOps = [];

    private readonly Dictionary<string, AttributeMetadata> _byName;
    private readonly RuleColumnCatalog _catalog = new();
    private readonly RuleMetadataLoader? _loader;
    private readonly IProfileService? _profiles;
    private readonly IAppNavigator? _navigator;
    private readonly RulesNavigationRequest? _request;
    private readonly IContentDialogService? _dialogs;
    private readonly ISnackbarService? _snackbar;
    private readonly ILogger<RuleEditorViewModel>? _logger;
    private readonly ILookupRecordPicker? _picker;
    private readonly RulePreviewController _preview = new();

    private string _table;
    private int _recordCount;
    private int _seed;
    private string _runId;
    private bool _suppressTableChange;
    private Profile? _profile;
    private Action<Profile>? _onSaved;
    private Type? _returnPage;
    private bool _isStored;

    private IReadOnlyList<RuleMessage> _messages = [];
    private FieldRule? _effectiveRule;
    private bool _suppressDraftLoad;
    private string? _lookupDraftError;
    private int _editorGeneration;
    private CancellationTokenSource? _pageCts;
    private CancellationTokenSource? _pickerCts;
    private SynchronizationContext? _uiContext;
    private HashSet<string> _errorProperties = [];

    /// <summary>Lookup identity editor state for the selected column.</summary>
    public LookupRuleInputViewModel LookupInput { get; }

    /// <summary>Bogus catalog editor state for the selected column.</summary>
    public BogusRuleInputViewModel BogusInput { get; }

    /// <summary>Initialises the editor from full live entity metadata (Task 9 supplies this via <c>IMetadataProvider</c>).</summary>
    /// <param name="meta">Full entity metadata — editor never derives columns from <c>EntitySummary</c> or creates a provider.</param>
    /// <param name="recordCount">Planned record count for the run (sequence overflow / pattern worst-case width).</param>
    /// <param name="seed">Generation seed — feeds the deterministic preview substream.</param>
    /// <param name="runId">Run id — feeds <c>{runId}</c> pattern expansion and its worst-case length.</param>
    public RuleEditorViewModel(EntityMetadata meta, int recordCount, int seed, string runId)
        : this(recordCount, seed, runId)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(runId);
        ResetFromMetadata(meta);
        IsMetadataAvailable = true;
    }

    /// <summary>Test convenience — wraps individual services into the DI types.</summary>
    internal RuleEditorViewModel(
        IMetadataProvider metadata,
        IProfileService profiles,
        IAppNavigator navigator,
        RulesNavigationRequest request,
        IContentDialogService? dialogs = null,
        ISnackbarService? snackbar = null,
        ILogger<RuleEditorViewModel>? logger = null,
        ILookupRecordPicker? picker = null,
        IDataverseConnectionService? connection = null)
        : this(
            new RuleMetadataLoader(metadata, connection),
            profiles,
            navigator,
            request,
            new RuleEditorServices(dialogs, snackbar, logger, picker))
    {
    }

    /// <summary>DI constructor for the Rules page. Call <see cref="LoadForProfileAsync"/> on navigate.</summary>
    [ActivatorUtilitiesConstructor]
    public RuleEditorViewModel(
        RuleMetadataLoader metadata,
        IProfileService profiles,
        IAppNavigator navigator,
        RulesNavigationRequest request,
        RuleEditorServices services)
        : this(10, 42, "rules-preview")
    {
        _loader = metadata;
        _profiles = profiles;
        _navigator = navigator;
        _request = request;
        _dialogs = services.Dialogs;
        _snackbar = services.Snackbar;
        _logger = services.Logger;
        _picker = services.Picker;
    }

    private RuleEditorViewModel(int recordCount, int seed, string runId)
    {
        _byName = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);
        _table = string.Empty;
        _recordCount = recordCount;
        _seed = seed;
        _runId = runId;
        LookupInput = new LookupRuleInputViewModel();
        LookupInput.Changed += OnLookupInputChanged;
        BogusInput = new BogusRuleInputViewModel();
        BogusInput.Changed += OnBogusInputChanged;
        BogusInput.PropertyChanged += OnBogusInputPropertyChanged;
        _preview.Changed += OnPreviewChanged;
    }

    /// <summary>True when the selected column is a lookup or customer attribute.</summary>
    public bool IsLookupColumn =>
        SelectedColumn is not null
        && _byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
        && attr is LookupAttributeMetadata;

    /// <summary>True when constant/one-of identity editing is shown for a lookup column.</summary>
    public bool IsLookupValueOperation =>
        IsLookupColumn && SelectedOp is "constant" or "oneOf";

    /// <summary>Run-time-only copy for lookupRandom preview. Interpolates the shared candidate bound.</summary>
    public string LookupRandomExplanation => RulePreviewController.LookupRandomExplanation;

    /// <summary>True when a metadata load failed and the page should show reconnect/retry guidance.</summary>
    public bool HasMetadataError => !string.IsNullOrWhiteSpace(MetadataError);

    /// <summary>True when live table metadata is loaded and save/picker may run.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isMetadataAvailable;

    /// <summary>True while table metadata is being fetched.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isMetadataLoading;

    /// <summary>Persistent reconnect/retry copy after a metadata or connection failure.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMetadataError))]
    private string? _metadataError;

    /// <summary>WR-002: the last profile-store failure. Shown in its own banner with Retry save.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStoreError))]
    private string? _storeError;

    /// <summary>True when the last profile write failed and the change is not saved.</summary>
    public bool HasStoreError => StoreError is not null;

    private Func<CancellationToken, Task>? _retryStoreWrite;

    // ── Page-scoped surface ──────────────────────────────────────────────────

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsWorkingSet))]
    private string _profileName = "Untitled";

    /// <summary>True when the loaded profile is the in-memory working set, not a stored profile.</summary>
    public bool IsWorkingSet => string.Equals(ProfileName, "working-set", StringComparison.Ordinal);

    /// <summary>Breadcrumb root. Generate when opened from the wizard; Profiles otherwise.</summary>
    public string BreadcrumbRootLabel =>
        _returnPage == typeof(GeneratePage) ? "Generate" : "Profiles";

    [ObservableProperty] private RuleTableOption? _selectedTable;

    /// <summary>Tables in the current profile (header switcher).</summary>
    public ObservableCollection<RuleTableOption> Tables { get; } = [];

    /// <summary>Mapped / Required / All chips.</summary>
    public ObservableCollection<ColumnFilterMode> ColumnFilterModes => _catalog.FilterModes;

    /// <summary>Live preview rows mapped from <see cref="PreviewValues"/>.</summary>
    public ObservableCollection<PreviewRow> PreviewRows => _preview.Rows;

    /// <summary>Insertable pattern tokens.</summary>
    public ObservableCollection<TokenChip> AvailableTokens { get; } = [new("{seq}"), new("{runId}"), new("{n}")];

    /// <summary>Preview pane footer — sampled from the table's planned row count.</summary>
    public string PreviewFooterLabel => $"Sampled from {_recordCount:N0} rows";

    /// <summary>Grouped view. Null after the metadata ctor — tests must not touch it.</summary>
    public ICollectionView? ColumnsView => _catalog.ColumnsView;

    // ── Picker ────────────────────────────────────────────────────────────────

    /// <summary>Eligible rule targets (RuleEligibility.Classify == Settable), filtered by <see cref="SearchText"/>.</summary>
    public IReadOnlyList<PickerColumn> SettableColumns => _catalog.Settable(SearchText);

    /// <summary>Platform-owned columns, grouped and disabled with their reason; stays findable while excluded (S3).</summary>
    public IReadOnlyList<PickerColumn> ExcludedColumns => _catalog.Excluded(SearchText);

    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(SettableColumns));
        OnPropertyChanged(nameof(ExcludedColumns));
        ColumnsView?.Refresh();
    }

    [ObservableProperty] private PickerColumn? _selectedColumn;

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
            // OnSelectedOpChanged is suppressed below, so the previous column's draft must be wiped here.
            BogusInput.ClearEditor();
            ConstantText = Template = MinText = MaxText = StartText = StepText = string.Empty;
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
            NotifyLookupPresentation();
            // Bogus is the most common choice, so it's the default wherever the column type offers it.
            SelectedOp = AvailableOps.Contains("bogus") ? "bogus" : AvailableOps.FirstOrDefault() ?? string.Empty;
            if (SelectedOp == "bogus")
                BogusInput.RefreshCatalog(TryTargetKind(out var kind) ? kind : null);
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

    [ObservableProperty] private string _selectedOp = string.Empty;

    partial void OnSelectedOpChanged(string value)
    {
        CancelPicker();
        NotifyLookupPresentation();
        if (_suppressDraftLoad)
            return;
        if (value == "bogus")
            BogusInput.RefreshCatalog(TryTargetKind(out var kind) ? kind : null);
        else if (!BogusInput.IsSuppressing)
            BogusInput.ClearEditor();
        Revalidate();
    }

    /// <summary>Selected Bogus API id, or null.</summary>
    public string? SelectedBogusApi
    {
        get => BogusInput.SelectedBogusApi;
        set => BogusInput.SelectedBogusApi = value;
    }

    /// <summary>Selected Bogus endpoint id (<c>API.endpoint</c>), or null.</summary>
    public string? SelectedBogusEndpoint
    {
        get => BogusInput.SelectedBogusEndpoint;
        set => BogusInput.SelectedBogusEndpoint = value;
    }

    /// <summary>Authored numeric minimum, or empty.</summary>
    public string BogusMinNumber
    {
        get => BogusInput.BogusMinNumber;
        set => BogusInput.BogusMinNumber = value;
    }

    /// <summary>Authored numeric maximum, or empty.</summary>
    public string BogusMaxNumber
    {
        get => BogusInput.BogusMaxNumber;
        set => BogusInput.BogusMaxNumber = value;
    }

    /// <summary>Authored length text, or empty.</summary>
    public string BogusLengthText
    {
        get => BogusInput.BogusLengthText;
        set => BogusInput.BogusLengthText = value;
    }

    /// <summary>Authored date minimum, or null.</summary>
    public DateTime? BogusMinDate
    {
        get => BogusInput.BogusMinDate;
        set => BogusInput.BogusMinDate = value;
    }

    /// <summary>Authored date maximum, or null.</summary>
    public DateTime? BogusMaxDate
    {
        get => BogusInput.BogusMaxDate;
        set => BogusInput.BogusMaxDate = value;
    }

    /// <inheritdoc />
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    bool INotifyDataErrorInfo.HasErrors => _messages.Any(m => m.Severity == RuleMessageSeverity.Error);

    /// <inheritdoc />
    public IEnumerable GetErrors(string? propertyName)
    {
        // Entity-level errors would adorn every {Binding} host, i.e. the whole operation template.
        // Rule-level messages already surface through HasMessages / InfoBarMessage.
        if (string.IsNullOrEmpty(propertyName))
            return Array.Empty<string>();

        return _messages
            .Where(m => PropertyNameFor(m.Target) == propertyName)
            .Select(m => m.Text)
            .ToList();
    }

    [ObservableProperty] private string _constantText = string.Empty;
    partial void OnConstantTextChanged(string value) => Revalidate();

    [ObservableProperty] private string _template = string.Empty;

    partial void OnTemplateChanged(string value) => Revalidate();

    [ObservableProperty] private string _minText = string.Empty;
    partial void OnMinTextChanged(string value) => Revalidate();

    [ObservableProperty] private string _maxText = string.Empty;
    partial void OnMaxTextChanged(string value) => Revalidate();

    [ObservableProperty] private string _startText = string.Empty;
    partial void OnStartTextChanged(string value) => Revalidate();

    [ObservableProperty] private string _stepText = string.Empty;
    partial void OnStepTextChanged(string value) => Revalidate();

    /// <summary>Checkable option subset for Choice/Two-Options <c>oneOf</c> rule.</summary>
    public ObservableCollection<OptionChoice> Options { get; } = [];

    [ObservableProperty] private OneOfPick _pick = OneOfPick.Random;
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
    public IReadOnlyList<string> PreviewValues => _preview.Values;

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
            ApplyLookupRule(rule);
            return;
        }

        ApplyScalarRule(rule);
    }

    private void ApplyLookupRule(FieldRule rule)
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
    }

    private void ApplyScalarRule(FieldRule rule)
    {
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
    public bool MatchesColumnFilter(PickerColumn column) =>
        _catalog.Matches(column, SearchText, IsRequired(column), IsMapped(column), _profile is not null);

    /// <summary>
    /// Reads <see cref="RulesNavigationRequest"/>, then loads tables + metadata.
    /// Creates <see cref="ColumnsView"/> only on this path (lock 24).
    /// </summary>
    public async Task LoadForProfileAsync(CancellationToken ct = default)
    {
        _onSaved = _request?.OnSaved;
        _returnPage = _request?.ReturnPage;
        _isStored = _request?.IsStored ?? false;
        SetStoreFailure(null, null); // a new profile on the page has no pending write
        OnPropertyChanged(nameof(BreadcrumbRootLabel));
        var profile = _request?.Profile;
        var tableName = _request?.TableName;
        _request?.Clear();

        if (profile is null)
        {
            ApplyMissingProfile();
            return;
        }

        ct.ThrowIfCancellationRequested();
        ApplyProfileHeader(profile, tableName);
        await ReloadMetadataAsync(ct);
    }

    private void ApplyMissingProfile()
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
        _catalog.Clear();
        OnPropertyChanged(nameof(SettableColumns));
        OnPropertyChanged(nameof(ExcludedColumns));
        OnPropertyChanged(nameof(ColumnsView));
        IsMetadataAvailable = false;
        MetadataError = "No profile is loaded. Open a profile, then retry.";
        NotifyCanSaveChanged();
        RetryMetadataCommand.NotifyCanExecuteChanged();
    }

    private void ApplyProfileHeader(Profile profile, string? tableName)
    {
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
    }

    private bool CanRetryMetadata() =>
        _profile is not null && _loader is not null && !IsMetadataLoading;

    /// <summary>Retries metadata load using the retained profile, save callback, and selected table.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryMetadata))]
    private Task RetryMetadataAsync(CancellationToken ct) => ReloadMetadataAsync(ct);

    private async Task ReloadMetadataAsync(CancellationToken ct)
    {
        if (_profile is null || _loader is null)
        {
            SetMetadataUnavailable("No profile is loaded. Open a profile, then retry.");
            return;
        }

        if (SelectedTable is null)
        {
            SetMetadataUnavailable("No table is selected. Choose a table, then retry.");
            return;
        }

        BeginMetadataLoad();
        var result = await _loader.FetchAsync(
            Tables.Select(t => t.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            _pageCts?.Token ?? CancellationToken.None,
            ct);
        try
        {
            if (!_loader.IsCurrent(result.Generation) || result.IsCanceled)
                return;
            ApplyFetchResult(result);
        }
        finally
        {
            if (_loader.IsCurrent(result.Generation))
                FinishMetadataLoad();
        }
    }

    private void BeginMetadataLoad()
    {
        IsMetadataLoading = true;
        MetadataError = null;
        NotifyReadyCommands();
    }

    private void FinishMetadataLoad()
    {
        IsMetadataLoading = false;
        NotifyReadyCommands();
    }

    private void SetMetadataUnavailable(string message)
    {
        IsMetadataAvailable = false;
        IsMetadataLoading = false;
        MetadataError = message;
        NotifyReadyCommands();
    }

    private void ApplyFetchResult(MetadataFetchResult result)
    {
        if (result.Exception is not null)
        {
            FailMetadata(result.Error ?? result.Exception.Message, result.Exception);
            return;
        }

        if (_loader is null || SelectedTable is null || !_loader.TryGet(SelectedTable.LogicalName, out var meta))
        {
            IsMetadataAvailable = false;
            MetadataError = "Table metadata was not returned. Reconnect and retry.";
            NotifyCanSaveChanged();
            return;
        }

        ApplyLoadedMetadata(meta);
    }

    private void ApplyLoadedMetadata(EntityMetadata meta)
    {
        var selectedName = SelectedColumn?.LogicalName;
        ResetFromMetadata(meta);
        _catalog.BindView(MatchesColumnFilter);
        OnPropertyChanged(nameof(ColumnsView));
        IsMetadataAvailable = true;
        MetadataError = null;
        if (selectedName is not null)
            SelectedColumn = _catalog.Find(selectedName);

        NotifyReadyCommands();
    }

    private void NotifyReadyCommands()
    {
        NotifyCanSaveChanged();
        RetryMetadataCommand.NotifyCanExecuteChanged();
        PickLookupRecordsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTableChanged(RuleTableOption? value)
    {
        CancelPicker();
        RemoveTableCommand.NotifyCanExecuteChanged();
        if (_suppressTableChange || value is null)
            return;

        ApplyTableCounts(value.LogicalName);
        if (_loader is null || !_loader.TryGet(value.LogicalName, out var meta))
            return;

        SelectedColumn = null;
        ResetFromMetadata(meta);
        _catalog.ApplyGrouping();
        ColumnsView?.Refresh();
        OnPropertyChanged(nameof(PreviewFooterLabel));
        SelectedColumn = _catalog.FirstSettable;
    }

    [RelayCommand]
    private void SetColumnFilterMode(ColumnFilterMode? mode)
    {
        if (mode is null || string.IsNullOrWhiteSpace(mode.Key))
            return;

        _catalog.SetFilterKey(mode.Key);
        _catalog.ApplyGrouping();
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
        _preview.Reroll();
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
        SaveProfileAsCommand.NotifyCanExecuteChanged();
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
        return rule is not null
               && await CommitColumnRuleAsync(_table, SelectedColumn.LogicalName, rule, "save the rule", ct);
    }

    /// <summary>Sets one column rule (or removes it when <paramref name="rule"/> is null) and commits it.</summary>
    private async Task<bool> CommitColumnRuleAsync(
        string table, string column, FieldRule? rule, string action, CancellationToken ct)
    {
        if (_profile is null
            || !_profile.Tables.Any(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!await CommitAsync(_profile.WithColumnRule(table, column, rule), action,
                c => CommitColumnRuleAsync(table, column, rule, action, c), ct))
            return false;

        RefreshMappedColumn();
        return true;
    }

    /// <summary>
    /// WR-002: the single store-commit path. <paramref name="next"/> becomes the committed profile only
    /// after the write succeeds; on failure the committed profile is untouched and Retry save replays
    /// <paramref name="retry"/>. Generate working-set snapshots are not in the store — callback only.
    /// </summary>
    /// <returns>False when the store write failed.</returns>
    private async Task<bool> CommitAsync(Profile next, string action, Func<CancellationToken, Task> retry,
        CancellationToken ct, bool alwaysWrite = false)
    {
        if (_profiles is null)
            return false;

        try
        {
            // CR-003: opened from Profiles, it's always written back. Opened from Generate, only when the
            // loaded profile has a file ("working-set" never does). ListAsync returns file stems, so
            // match the stem this name saves to, not the raw name. Save As always writes.
            var stem = JsonProfileService.Sanitize(next.Name);
            if (alwaysWrite || _isStored
                || (await _profiles.ListAsync(ct)).Any(n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase)))
                await _profiles.SaveAsync(next, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailProfileStore(action, retry, ex);
            return false;
        }

        _profile = next;
        SetStoreFailure(null, null);
        _onSaved?.Invoke(next);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanRetrySave))]
    private Task RetrySaveAsync(CancellationToken ct) => _retryStoreWrite?.Invoke(ct) ?? Task.CompletedTask;

    private bool CanRetrySave() => _retryStoreWrite is not null;

    /// <summary>Commits the current rule without leaving the page (Preview pane's Save button).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveRuleAsync(CancellationToken ct) => await CommitSelectedColumnRuleAsync(ct);

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync(CancellationToken ct)
    {
        // "working-set" isn't a stored profile — route to Save As so it always gets a real name.
        if (IsWorkingSet)
        {
            await SaveProfileAsAsync(ct);
            return;
        }

        if (!await CommitSelectedColumnRuleAsync(ct))
            return;

        if (_returnPage is not null)
            _navigator?.Navigate(_returnPage);
    }

    /// <summary>Test seam for <see cref="AskProfileNameAsync"/> — bypasses the real dialog.</summary>
    internal Func<string, Task<string?>>? PromptProfileName { get; set; }

    private async Task<string?> AskProfileNameAsync(string suggested)
    {
        if (PromptProfileName is not null)
            return await PromptProfileName(suggested);
        return _dialogs is null ? null : await _dialogs.AskProfileNameAsync(suggested);
    }

    /// <summary>Prompts for a name and saves the current draft as a new profile, leaving the
    /// original (if any) untouched.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsAsync(CancellationToken ct)
    {
        if (_profiles is null || _profile is null)
            return;

        var name = await AskProfileNameAsync(IsWorkingSet ? "" : ProfileName);
        if (name is null || !JsonProfileService.IsValidName(name))
            return;

        var rule = BuildRule();
        if (!await SaveAsCoreAsync(name, _table, SelectedColumn?.LogicalName, rule, ct))
            return;

        if (_returnPage is not null)
            _navigator?.Navigate(_returnPage);
    }

    private async Task<bool> SaveAsCoreAsync(
        string name, string table, string? column, FieldRule? rule, CancellationToken ct)
    {
        if (_profile is null)
            return false;

        var next = (rule is null || column is null ? _profile : _profile.WithColumnRule(table, column, rule))
            with { Name = name };
        if (!await CommitAsync(next, "save the rule",
                c => SaveAsCoreAsync(name, table, column, rule, c), ct, alwaysWrite: true))
            return false;

        _isStored = true; // CR-003: it now has a file, so later rule edits persist
        ProfileName = name;
        RefreshMappedColumn();
        return true;
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

        return await _dialogs.ConfirmAsync(
            "Delete rule", $"Delete the rule for '{columnDisplayName}'? This cannot be undone.", "Delete");
    }

    /// <summary>Removes the current column's rule entirely (reverts it to Unmapped), after confirm.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteRule))]
    private async Task DeleteRuleAsync(CancellationToken ct)
    {
        if (_profiles is null || _profile is null || SelectedColumn is null)
            return;
        if (!await ConfirmDeleteRuleAsync(SelectedColumn.DisplayName))
            return;

        await CommitColumnRuleAsync(_table, SelectedColumn.LogicalName, null, "delete the rule", ct);
    }

    // A profile needs at least one table (the store rejects an empty list), so the last one stays.
    private bool CanRemoveTable() =>
        _profiles is not null && _profile is { Tables.Count: > 1 } && SelectedTable is not null;

    /// <summary>Test seam for <see cref="ConfirmRemoveTableAsync"/> — bypasses the real dialog.</summary>
    internal Func<string, Task<bool>>? ConfirmRemoveTable { get; set; }

    private async Task<bool> ConfirmRemoveTableAsync(string table)
    {
        if (ConfirmRemoveTable is not null)
            return await ConfirmRemoveTable(table);
        if (_dialogs is null)
            return false;

        return await _dialogs.ConfirmAsync(
            "Remove table?",
            $"Are you sure you want to remove '{table}' and all of its rules from '{ProfileName}'? This cannot be undone.",
            "Remove");
    }

    /// <summary>Drops the selected table — its row count and every rule on it — from the profile, after confirm.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveTable))]
    private async Task RemoveTableAsync(CancellationToken ct)
    {
        if (_profile is null || SelectedTable is not { } table)
            return;
        if (!await ConfirmRemoveTableAsync(table.DisplayName))
            return;

        await RemoveTableCoreAsync(table, ct);
    }

    private async Task<bool> RemoveTableCoreAsync(RuleTableOption table, CancellationToken ct)
    {
        if (_profile is null)
            return false;

        var next = _profile with
        {
            Tables = _profile.Tables
                .Where(t => !string.Equals(t.Table, table.LogicalName, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };
        if (!await CommitAsync(next, "remove the table", c => RemoveTableCoreAsync(table, c), ct))
            return false; // the switcher still lists the table, matching the unchanged profile

        SelectedTable = Tables.First(t => t != table);
        Tables.Remove(table);
        return true;
    }

    private void RefreshMappedColumn()
    {
        // ResetFromMetadata rebuilds every PickerColumn record; re-point SelectedColumn at its
        // fresh instance by name or the ListBox loses its highlight when the mapped state flips.
        if (_loader is null || SelectedColumn is null || !_loader.TryGet(_table, out var meta))
            return;
        var name = SelectedColumn.LogicalName;
        ResetFromMetadata(meta);
        ColumnsView?.Refresh();
        SelectedColumn = _catalog.Find(name) ?? SelectedColumn;
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
            "bogus" => BogusInput.TryBuild(),
            _ => null,
        };
    }

    private FieldRule? TryOneOf(AttributeMetadata attr)
    {
        List<JsonElement> values;
        if (attr is EnumAttributeMetadata or BooleanAttributeMetadata)
        {
            values = Options.Where(o => o.IsChecked)
                .Select(o =>
                    JsonDocument.Parse(o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement)
                .ToList();
        }
        else
        {
            // ponytail: literal-list entry for non-choice scalars piggybacks on ConstantText as a
            // comma-separated list — a dedicated multi-value editor is out of scope for this task.
            if (string.IsNullOrWhiteSpace(ConstantText)) return null;
            values = [];
            foreach (var token in ConstantText.Split(',',
                         StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
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

    private FieldRule? TrySequence() =>
        TryInvariantNumber(StartText, 0m, out var start) && TryInvariantNumber(StepText, 1m, out var step)
            ? new SequenceRule(start, step)
            : null;

    // WR-007: invariant, like the restore path (ApplyScalarRule) and RuleValidator's numbers.
    // Blank keeps the default; anything else that doesn't parse is a validation error.
    private static bool TryInvariantNumber(string? text, decimal fallback, out decimal value)
    {
        value = fallback;
        return string.IsNullOrWhiteSpace(text)
               || decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryConstantValue(AttributeMetadata attr, string? text, out JsonElement value)
    {
        value = default;
        if (text is null) return false;
        try
        {
            var json = attr switch
            {
                StringAttributeMetadata or MemoAttributeMetadata => JsonSerializer.Serialize(text),
                // WR-007: typed in the user's culture, stored as round-trip UTC text that Core reads invariantly.
                DateTimeAttributeMetadata => JsonSerializer.Serialize(DateTime.Parse(
                        text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                    .ToString("o", CultureInfo.InvariantCulture)),
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

    // ── Picker copy (§3.2) ───────────────────────────────────────────────────

    private void ResetFromMetadata(EntityMetadata meta)
    {
        _table = meta.LogicalName ?? string.Empty;
        _catalog.Reset(meta, _byName, IsMappedName, IsRequired);
        OnPropertyChanged(nameof(SettableColumns));
        OnPropertyChanged(nameof(ExcludedColumns));
        OnPropertyChanged(nameof(PreviewFooterLabel));
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
        _byName.TryGetValue(column.LogicalName, out var attr)
        && attr.RequiredLevel?.Value is AttributeRequiredLevel.SystemRequired
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

    private static IReadOnlyList<string> OpsFor(AttributeMetadata attr)
    {
        if (attr is LookupAttributeMetadata)
            return RuleEligibility.Classify(attr).IsSettable
                ? ["constant", "oneOf", "lookupRandom", "null"]
                : NoOps;

        var ops = attr switch
        {
            StringAttributeMetadata or MemoAttributeMetadata => TextOps,
            IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata
                or MoneyAttributeMetadata => NumericOps,
            DoubleAttributeMetadata => FloatOps,
            DateTimeAttributeMetadata => DateOps,
            BooleanAttributeMetadata => ChoiceOps,
            EnumAttributeMetadata => ChoiceOps,
            _ => NoOps,
        };

        if (attr is EnumAttributeMetadata)
            return ops;
        if (RuleValidator.TryMapValueKind(attr, out var kind) && BogusCatalogQuery.HasAny(kind))
            return [.. ops, "bogus"];
        return ops;
    }

    private bool TryTargetKind(out DataverseValueKind kind)
    {
        kind = default;
        return SelectedColumn is not null
               && _byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
               && RuleValidator.TryMapValueKind(attr, out kind);
    }

    private void RestoreBogus(BogusRule rule)
    {
        var previous = _suppressDraftLoad;
        _suppressDraftLoad = true;
        try
        {
            SelectedOp = "bogus";
            BogusInput.Restore(rule, TryTargetKind(out var kind) ? kind : null);
        }
        finally
        {
            _suppressDraftLoad = previous;
        }

        Revalidate();
    }

    private void OnBogusInputChanged(object? sender, EventArgs e)
    {
        if (!_suppressDraftLoad)
            Revalidate();
    }

    private void OnBogusInputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName))
            OnPropertyChanged(e.PropertyName);
    }

    private void SchedulePreview()
    {
        AttributeMetadata? attr = null;
        if (SelectedColumn is not null)
            _byName.TryGetValue(SelectedColumn.LogicalName, out attr);
        _preview.Schedule(_effectiveRule, attr, _seed, _table, _runId, _recordCount);
    }

    private void OnPreviewChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(PreviewValues));

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
        RuleInputTarget.Api => nameof(SelectedBogusApi),
        RuleInputTarget.Endpoint => nameof(SelectedBogusEndpoint),
        RuleInputTarget.Minimum => nameof(BogusMinNumber),
        RuleInputTarget.Maximum => nameof(BogusMaxNumber),
        RuleInputTarget.Length => nameof(BogusLengthText),
        RuleInputTarget.MinimumDate => nameof(BogusMinDate),
        RuleInputTarget.MaximumDate => nameof(BogusMaxDate),
        _ => null,   // Rule-level errors belong to the InfoBar, not to one input's adorner.
    };

    /// <summary>Starts page lifetime: captures the UI context and listens for connection resets.</summary>
    public void Activate()
    {
        _uiContext = SynchronizationContext.Current;
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = new CancellationTokenSource();
        _loader?.SubscribeReset(OnConnectionReset);
    }

    /// <summary>Ends page lifetime and cancels picker, preview, and metadata work.</summary>
    public void Deactivate()
    {
        _loader?.UnsubscribeReset(OnConnectionReset);
        Interlocked.Increment(ref _editorGeneration);
        _preview.Cancel();
        _loader?.Cancel();
        _pageCts?.Cancel();
        _pickerCts?.Cancel();
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

        if (IsStalePickerResult(result, token, generation, table, column, op) || result is null)
            return;
        AcceptPickerSelection(column, result);
    }

    private bool IsStalePickerResult(
        IReadOnlyList<LookupRuleValue>? result,
        CancellationToken token,
        int generation,
        string table,
        string column,
        string op) =>
        result is null
        || token.IsCancellationRequested
        || generation != _editorGeneration
        || !string.Equals(table, _table, StringComparison.OrdinalIgnoreCase)
        || SelectedColumn is null
        || !string.Equals(column, SelectedColumn.LogicalName, StringComparison.OrdinalIgnoreCase)
        || SelectedOp != op;

    private void AcceptPickerSelection(string column, IReadOnlyList<LookupRuleValue> result)
    {
        if (!_byName.TryGetValue(column, out var currentAttr) ||
            currentAttr is not LookupAttributeMetadata currentLookup)
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
        _preview.Cancel();
        _loader?.Cancel();
        _pickerCts?.Cancel();
    }

    private void ApplyConnectionResetUi()
    {
        _loader?.ClearEntities();
        IsMetadataAvailable = false;
        IsMetadataLoading = false;
        MetadataError = "Connection changed. Reconnect and retry to reload table metadata.";
        _preview.Clear();
        NotifyReadyCommands();
    }

    private void FailMetadata(string message, Exception ex)
    {
        RuleMetadataLoader.LogFailure(_logger, _snackbar, ex, message);
        IsMetadataAvailable = false;
        MetadataError = string.IsNullOrWhiteSpace(message)
            ? "Couldn't load table metadata. Reconnect and retry."
            : $"{message} Reconnect and retry.";
        NotifyReadyCommands();
    }

    /// <summary>
    /// WR-002: a profile-store failure gets its own banner and Retry save. The metadata banner's Retry
    /// reloads metadata and cannot redo the write.
    /// </summary>
    private void FailProfileStore(string action, Func<CancellationToken, Task> retry, Exception ex)
    {
        _logger?.LogError(ex, "Failed to {Action}", action);
        _snackbar?.Show($"Couldn't {action}", ex.Message,
            ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        SetStoreFailure($"Couldn't {action}: {ex.Message}", retry);
    }

    private void SetStoreFailure(string? error, Func<CancellationToken, Task>? retry)
    {
        StoreError = error;
        _retryStoreWrite = retry;
        RetrySaveCommand.NotifyCanExecuteChanged();
    }
}
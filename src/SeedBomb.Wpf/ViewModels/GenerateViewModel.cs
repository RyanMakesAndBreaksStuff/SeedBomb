using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeedBomb.Core.Contracts;
using SeedBomb.Core.Generators;
using SeedBomb.Core.Metadata;
using SeedBomb.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using SeedBomb.Services.Generation;
using SeedBomb.Services.History;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels.Controls;
using SeedBomb.Views.Pages;
using System.Collections.ObjectModel;
using Wpf.Ui;
using Wpf.Ui.Extensions;

namespace SeedBomb.ViewModels;

/// <summary>Step entry in the horizontal stepper strip above the wizard cards.</summary>
/// <param name="Glyph">Displayed circle glyph.</param>
/// <param name="Label">Displayed step label.</param>
/// <param name="IsDone">Whether the step has been completed.</param>
/// <param name="IsActive">Whether the step is the current active step.</param>
/// <param name="HasNext">Whether a connector line should be drawn after this step.</param>
public record StepEntry(string Glyph, string Label, bool IsDone, bool IsActive, bool HasNext);

/// <summary>One selected table in the step-1 aside.</summary>
public sealed record SelectedTableRow(string DisplayName, string LogicalName, int Count);

/// <summary>Entity queue status entry. Rendering is the view's concern; this holds no brushes.</summary>
public sealed class QueuedEntityEntry(EntitySummary entity)
{
    /// <summary>Gets the entity summary.</summary>
    public EntitySummary Entity { get; } = entity;
}

/// <summary>Review-card preview: first five <see cref="RuleValueGenerator"/> outputs for one ruled column.</summary>
/// <param name="Table">Owning table logical name.</param>
/// <param name="Column">Column logical name.</param>
/// <param name="DisplayName">Column display name.</param>
/// <param name="Values">Up to five formatted preview values (row 0..4).</param>
public sealed record ReviewPreviewRow(
    string Table,
    string Column,
    string DisplayName,
    IReadOnlyList<string> Values)
{
    /// <summary>Values joined for the 828px review row.</summary>
    public string SampleLine => string.Join(" · ", Values);
}

/// <summary>ViewModel for the Generate wizard page.</summary>
public sealed partial class GenerateViewModel : ViewModelBase, IDisposable
{
    private readonly IRunHistoryService _historyService;
    private readonly ISettingsService _settingsService;
    private readonly ISnackbarService _snackbar;
    private readonly IMetadataProvider _metadataProvider;
    private readonly IContentDialogService _contentDialogService;
    private readonly ILogger<GenerateViewModel> _logger;
    private readonly IAppNavigator? _navigator;
    private readonly MainWindowViewModel? _mainWindow;
    private readonly GenerateDraftAutosave _draftAutosave;
    private readonly GenerateProfileBridge _profileBridge;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _rulesCts;
    private FieldOverridesViewModel? _fieldOverrides;
    private FieldRulesViewModel _fieldRules;
    private EntitySelectorViewModel? _entitySelector;

    private Dictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata> _entityMetadata =
        new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedTableRows), nameof(PlannedTotal))]
    private int _defaultRecordCount = 10;

    /// <summary>Initialises the view-model.</summary>
    /// <param name="historyService">Run history persistence service.</param>
    /// <param name="settingsService">Settings persistence service, for the configured default record count.</param>
    /// <param name="snackbar">Snackbar notification service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metadataProvider">Metadata provider — supplies full <c>EntityMetadata</c> for the Rules step and preflight.</param>
    /// <param name="run">Singleton run sheet / first-run executor. Required.</param>
    /// <param name="rulesRequest">Optional payload for navigating to <see cref="RulesPage"/>.</param>
    /// <param name="navigator">Optional shell navigator.</param>
    /// <param name="mainWindow">Shell view-model; when provided, Generate reloads on connection switch.</param>
    public GenerateViewModel(
        IRunHistoryService historyService,
        ISettingsService settingsService,
        ISnackbarService snackbar,
        ILogger<GenerateViewModel> logger,
        IMetadataProvider metadataProvider,
        IProfileService profileService,
        IContentDialogService contentDialogService,
        RunViewModel run,
        RulesNavigationRequest? rulesRequest = null,
        IAppNavigator? navigator = null,
        MainWindowViewModel? mainWindow = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        _historyService = historyService;
        _settingsService = settingsService;
        _snackbar = snackbar;
        _logger = logger;
        _metadataProvider = metadataProvider;
        _contentDialogService = contentDialogService;
        _navigator = navigator;
        _mainWindow = mainWindow;
        Run = run;
        _fieldRules = new FieldRulesViewModel();
        AttachFieldRules(_fieldRules);
        _profileBridge = new GenerateProfileBridge(this, profileService, rulesRequest, logger);
        _draftAutosave = new GenerateDraftAutosave(
            profileService,
            logger,
            () => SelectedEntities.Count > 0 || (_fieldRules is not null && _fieldRules.GetRules().Count > 0),
            BuildProfileSnapshot);
        if (_mainWindow is not null)
            _mainWindow.ConnectionReloadRequested += OnConnectionReloadRequested;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_mainWindow is not null)
            _mainWindow.ConnectionReloadRequested -= OnConnectionReloadRequested;
    }

    private void OnConnectionReloadRequested(object? sender, EventArgs e) =>
        _ = ReloadForConnectionSwitchAsync();

    private async Task ReloadForConnectionSwitchAsync()
    {
        try
        {
            _entitySelector?.ClearSelection();
            await ResetWithoutPromptAsync();
            _entitySelector?.LoadEntitiesCommand.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload Generate after a connection switch");
        }
    }

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            DefaultRecordCount = settings.DefaultRecordCount;

            var wizardDirty = SelectedEntities.Count > 0 || CurrentStep > 0;
            if (!wizardDirty)
            {
                BatchSize = settings.DefaultBatchSize;
                MaxParallelism = settings.DefaultDop;
            }

            if (wizardDirty)
                return;

            await _profileBridge.LoadDraftAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise the Generate page");
            _snackbar.Show("Couldn't load the Generate page", ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    // ── State ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Steps),
        nameof(PlannedTotal),
        nameof(SelectedTableRows),
        nameof(ProfileSummaryLine),
        nameof(RunConfirmationLine),
        nameof(RunPlanStats))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private IReadOnlyList<EntitySummary> _selectedEntities = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Steps))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToReviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _isRunning;

    [ObservableProperty] private ProgressUpdate? _currentProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasResult), nameof(Steps),
        nameof(LastRunHasErrors), nameof(LastRunStatusText))]
    private GenerationResult? _lastResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunConfirmationLine), nameof(RunPlanStats), nameof(ProfileSummaryLine))]
    private int _seed = 42;

    /// <summary>Run-level Bogus locale. Read-only; always <c>en</c> until a picker ships.</summary>
    public string Locale => DeterministicFaker.DefaultLocale;

    [ObservableProperty] private int _batchSize = 500;
    [ObservableProperty] private int _maxParallelism;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Steps))]
    [NotifyCanExecuteChangedFor(nameof(GoToReviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _isRulesLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Steps), nameof(NextButtonLabel), nameof(StepProgressLabel), nameof(IsReviewOpen))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private int _currentStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Steps), nameof(ReviewedRuleCount), nameof(RulesLinkLabel), nameof(RunPlanStats),
        nameof(ProfileSummaryLine))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private Dictionary<string, Dictionary<string, FieldRule>>? _reviewedRules;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReviewErrorSummary))]
    private IReadOnlyList<RuleMessage> _reviewMessages = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _reviewHasErrors;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private long? _reviewedDraftRevision;

    [ObservableProperty] private string _runId = "";

    [ObservableProperty] private IReadOnlyList<ReviewPreviewRow> _reviewPreviewRows = [];

    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>Owned field-rules draft. Tests may swap via <see cref="AttachFieldRules"/>.</summary>
    public FieldRulesViewModel FieldRules => _fieldRules;

    internal FieldOverridesViewModel? FieldOverrides => _fieldOverrides;
    internal EntitySelectorViewModel? EntitySelector => _entitySelector;

    internal IReadOnlyDictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata> LiveEntityMetadata =>
        _entityMetadata;

    /// <summary>Singleton run sheet bound by the overlay.</summary>
    public RunViewModel Run { get; }

    /// <summary>True when the wizard is on Review or Run. Setter maps old two-state paging onto <see cref="CurrentStep"/>.</summary>
    public bool IsReviewOpen
    {
        get => CurrentStep >= 2;
        set
        {
            if (value && CurrentStep < 2) CurrentStep = 2;
            if (!value && CurrentStep >= 2) CurrentStep = 1;
        }
    }

    /// <summary>Footer primary-button caption.</summary>
    public string NextButtonLabel => CurrentStep == 3 ? "Start run" : "Next";

    /// <summary>Footer step indicator.</summary>
    public string StepProgressLabel => CurrentStep switch
    {
        0 => "Step 1 of 4 · Tables",
        1 => "Step 2 of 4 · Volume & rules",
        2 => "Step 3 of 4 · Review",
        _ => "Step 4 of 4 · Run",
    };

    /// <summary>Volume-step link out to the Rules page.</summary>
    public string RulesLinkLabel => $"{DraftRuleCount} rules · edit";

    /// <summary>Joined preflight errors for the review banner.</summary>
    public string ReviewErrorSummary =>
        string.Join(
            Environment.NewLine,
            ReviewMessages.Where(m => m.Severity == RuleMessageSeverity.Error).Select(m => m.Text));

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProfileSummaryLine))]
    private string _activeProfileName = "No profile loaded";

    /// <summary>Step-1 SELECTED rows. Counts come from FieldOverrides when attached, else <see cref="DefaultRecordCount"/>.</summary>
    public IReadOnlyList<SelectedTableRow> SelectedTableRows
    {
        get
        {
            var counts = _fieldOverrides?.GetCounts();
            return
            [
                .. SelectedEntities.Select(e => new SelectedTableRow(
                    e.DisplayName,
                    e.LogicalName,
                    counts is not null && counts.TryGetValue(e.LogicalName, out var n) ? n : DefaultRecordCount))
            ];
        }
    }

    /// <summary>PROFILE card second line.</summary>
    public string ProfileSummaryLine =>
        $"{DraftRuleCount} rules · seed {Seed} · {Locale}";

    /// <summary>
    /// Rules currently on the board. <see cref="ReviewedRuleCount"/> only becomes non-zero after
    /// preflight (step 3), so it reads "0 rules" for a profile just loaded onto step 1.
    /// </summary>
    public int DraftRuleCount => _fieldRules?.GetRules().Sum(t => t.Value.Count) ?? 0;

    /// <summary>Handoff alias used by the mock Change profile button.</summary>
    public IRelayCommand ChangeProfileCommand => OpenProfilesCommand;

    /// <summary>Sum of per-table record counts.</summary>
    public int PlannedTotal
    {
        get
        {
            var counts = _fieldOverrides?.GetCounts();
            return SelectedEntities.Sum(e =>
                counts is not null && counts.TryGetValue(e.LogicalName, out var n)
                    ? n
                    : DefaultRecordCount);
        }
    }

    /// <summary>Step 4 confirmation sentence, naming the environment the run writes to (CR-002).</summary>
    public string RunConfirmationLine =>
        $"Write {PlannedTotal:N0} rows across {SelectedEntities.Count} table(s) to "
        + $"{(Run.TargetHost is { Length: > 0 } host ? host : "the connected environment")} "
        + $"using seed {Seed}. Nothing is written until you start.";

    /// <summary>Step 4 stat tiles.</summary>
    public IReadOnlyList<RunValueRow> RunPlanStats =>
    [
        new("Tables", SelectedEntities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Rows", PlannedTotal.ToString("N0")),
        new("Rules", ReviewedRuleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Seed", Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Locale", Locale),
    ];

    /// <summary>Gets a value indicating whether a result is available to display.</summary>
    public bool HasResult => LastResult is not null;

    public bool LastRunHasErrors => LastResult is { Errors.Count: > 0 };

    public string LastRunStatusText =>
        LastResult is null
            ? string.Empty
            : LastResult.Errors.Count == 0
                ? "All entities succeeded"
                : LastResult.Errors.Count == 1
                    ? LastResult.Errors[0].ErrorMessage
                    : $"{LastResult.Errors.Count} batch errors";

    /// <summary>Gets the collection of queued entity status dots.</summary>
    public ObservableCollection<QueuedEntityEntry> QueuedEntities { get; } = [];

    /// <summary>Full live entity metadata for selected entities, loaded when advancing to Rules.</summary>
    public IReadOnlyDictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata> EntityMetadataMap => _entityMetadata;

    /// <summary>Total active rule count across every table in the reviewed snapshot (Review summary).</summary>
    public int ReviewedRuleCount => ReviewedRules?.Sum(t => t.Value.Count) ?? 0;

    /// <summary>Gets the current stepper step entries.</summary>
    public IReadOnlyList<StepEntry> Steps =>
    [
        new(CurrentStep > 0 ? "✓" : "1", "Tables", CurrentStep > 0, CurrentStep == 0, true),
        new(CurrentStep > 1 ? "✓" : "2", "Volume & rules", CurrentStep > 1, CurrentStep == 1, true),
        new(CurrentStep > 2 ? "✓" : "3", "Review", CurrentStep > 2, CurrentStep == 2, true),
        new(HasResult ? "✓" : "4", "Run", HasResult, CurrentStep == 3, false),
    ];

    // ── Commands ──────────────────────────────────────────────────────────────

    private bool CanGoBack() => CurrentStep > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => CurrentStep--;

    private bool CanGoNext() => CurrentStep switch
    {
        0 => SelectedEntities.Count > 0 && !IsRunning,
        1 => IsRulesLoaded && !IsRunning,
        2 => ReviewedRules is not null && !ReviewHasErrors && !IsRunning,
        3 => CanStartGenerate(),
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task GoNextAsync()
    {
        switch (CurrentStep)
        {
            case 0:
                await GoToRulesCommand.ExecuteAsync(null);
                if (IsRulesLoaded) CurrentStep = 1;
                break;
            case 1:
                if (GoToReviewCommand.CanExecute(null))
                    GoToReviewCommand.Execute(null);
                if (ReviewedRules is not null) CurrentStep = 2;
                break;
            case 2:
                CurrentStep = 3;
                break;
            case 3:
                await GenerateCommand.ExecuteAsync(null);
                break;
        }
    }

    /// <summary>
    /// Attaches the <see cref="FieldOverridesViewModel"/> instance owned by the page's
    /// <see cref="SeedBomb.Views.Controls.FieldOverridesControl"/> so counts can be
    /// read at generation time.
    /// </summary>
    /// <param name="vm">The FieldOverrides view-model.</param>
    public void AttachFieldOverrides(FieldOverridesViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        if (_fieldOverrides is not null)
        {
            _fieldOverrides.Entries.CollectionChanged -= OnCountEntriesChanged;
            foreach (var entry in _fieldOverrides.Entries)
                entry.PropertyChanged -= OnCountEntryChanged;
        }

        _fieldOverrides = vm;
        vm.Entries.CollectionChanged += OnCountEntriesChanged;
        foreach (var entry in vm.Entries)
            entry.PropertyChanged += OnCountEntryChanged;
    }

    /// <summary>
    /// Attaches the <see cref="EntitySelectorViewModel"/> instance owned by the page's
    /// <see cref="SeedBomb.Views.Controls.EntitySelectorControl"/> so a table selection made
    /// programmatically (e.g. <see cref="ApplyImportReport"/>) is mirrored into the picker's
    /// checkboxes rather than only updating <see cref="SelectedEntities"/> internally.
    /// </summary>
    /// <param name="vm">The entity selector picker's view-model.</param>
    public void AttachEntitySelector(EntitySelectorViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _entitySelector = vm;
    }

    // WR-003: SetEntities clears and rebuilds Entries, so per-entry handlers must be
    // re-attached on every collection change — not once at attach time.
    private void OnCountEntriesChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var old in e.OldItems?.OfType<EntityCountEntry>() ?? [])
            old.PropertyChanged -= OnCountEntryChanged;
        foreach (var added in e.NewItems?.OfType<EntityCountEntry>() ?? [])
            added.PropertyChanged += OnCountEntryChanged;

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset
            && _fieldOverrides is not null)
        {
            foreach (var entry in _fieldOverrides.Entries)
            {
                entry.PropertyChanged -= OnCountEntryChanged;
                entry.PropertyChanged += OnCountEntryChanged;
            }
        }

        NotifyVolumeChanged();
    }

    private void OnCountEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EntityCountEntry.Count))
            NotifyVolumeChanged();
    }

    private void NotifyVolumeChanged()
    {
        OnPropertyChanged(nameof(PlannedTotal));
        OnPropertyChanged(nameof(RunConfirmationLine));
        OnPropertyChanged(nameof(RunPlanStats));
        OnPropertyChanged(nameof(SelectedTableRows));
    }

    /// <summary>True when the rules board holds uncommitted edits.</summary>
    public bool IsBoardDirty() => _fieldRules?.IsDirty == true;

    /// <summary>
    /// Replaces the owned <see cref="FieldRulesViewModel"/>. Tests swap a fixture; production
    /// keeps the ctor-owned instance.
    /// </summary>
    /// <param name="vm">The FieldRules board view-model.</param>
    public void AttachFieldRules(FieldRulesViewModel vm)
    {
        if (_fieldRules is not null)
            _fieldRules.DraftChanged -= OnFieldRulesDraftChanged;
        _fieldRules = vm;
        _fieldRules.DraftChanged += OnFieldRulesDraftChanged;
    }

    // Any draft mutation invalidates the reviewed snapshot — Start stays locked until preflight reruns.
    private void OnFieldRulesDraftChanged(object? sender, EventArgs e)
    {
        ReviewedRules = null;
        ReviewMessages = [];
        ReviewHasErrors = false;
        ReviewedDraftRevision = null;
        ReviewPreviewRows = [];
        OnPropertyChanged(nameof(DraftRuleCount));
        OnPropertyChanged(nameof(RulesLinkLabel));
        OnPropertyChanged(nameof(ProfileSummaryLine));
        _draftAutosave.Schedule();
    }

    /// <summary>Called by the page when entity selection changes.</summary>
    /// <param name="entities">Newly selected entities.</param>
    public void OnEntitiesChanged(IReadOnlyList<EntitySummary> entities)
    {
        var incoming = entities.Select(e => e.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = SelectedEntities.Select(e => e.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sameSet = incoming.SetEquals(current);

        SelectedEntities = entities;
        _fieldOverrides?.SetEntities(entities, DefaultRecordCount);
        OnPropertyChanged(nameof(SelectedTableRows));

        QueuedEntities.Clear();
        foreach (var e in entities)
            QueuedEntities.Add(new QueuedEntityEntry(e));

        if (sameSet)
            return;

        IsRulesLoaded = false;
        CurrentStep = 0;
        ReviewedRules = null;
        ReviewMessages = [];
        ReviewHasErrors = false;
        ReviewedDraftRevision = null;
        ReviewPreviewRows = [];
    }

    /// <summary>
    /// Advances to the Rules step: loads full live metadata for the selected entities
    /// (S3 — UI command path, reuses the injected provider's cache) and stamps this run's id.
    /// </summary>
    [RelayCommand]
    private async Task GoToRulesAsync()
    {
        _rulesCts?.Cancel();
        _rulesCts?.Dispose();
        _rulesCts = new CancellationTokenSource();
        var ct = _rulesCts.Token;

        try
        {
            var names = SelectedEntities.Select(e => e.LogicalName).ToArray();
            var list = await _metadataProvider.GetEntitiesAsync(names, ct);
            _entityMetadata = list.Where(m => m.LogicalName is not null)
                .ToDictionary(m => m.LogicalName!, StringComparer.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(EntityMetadataMap));

            RunId = $"run-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            IsRulesLoaded = true;
            _fieldRules?.SelectTable(SelectedEntities.FirstOrDefault()?.LogicalName ?? string.Empty);
            _profileBridge.TryApplyRestoredDraft();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Rules metadata load cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load metadata for the Rules step");
            _snackbar.Show(
                "Rules metadata failed",
                ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger,
                null,
                TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Runs preflight (Task 4 <see cref="RuleValidator"/>, one-path with BulkCreator's own gate) over
    /// every draft rule, writes the immutable effective-rule snapshot Start will use verbatim, and
    /// renders the first-five <see cref="RuleValueGenerator"/> preview values for active rules.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoToReview))]
    private void GoToReview()
    {
        if (_fieldRules is null)
            return;

        var rawCounts = _fieldOverrides?.GetCounts() ?? new Dictionary<string, int>();
        var draft = _fieldRules.GetRules();
        var reviewed = new Dictionary<string, Dictionary<string, FieldRule>>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<RuleMessage>();
        var previewRows = new List<ReviewPreviewRow>();
        var hasErrors = false;

        foreach (var (table, columns) in draft)
        {
            if (!_entityMetadata.TryGetValue(table, out var meta))
            {
                messages.Add(new RuleMessage(RuleMessageSeverity.Error,
                    $"Metadata for '{table}' is unavailable — its rules were not reviewed."));
                hasErrors = true;
                continue;
            }

            var recordCount = rawCounts.GetValueOrDefault(table, DefaultRecordCount);
            var tableRules = new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase);

            foreach (var (column, rule) in columns)
            {
                var attr = meta.Attributes?.FirstOrDefault(a =>
                    string.Equals(a.LogicalName, column, StringComparison.OrdinalIgnoreCase));
                if (attr is null)
                {
                    messages.Add(new RuleMessage(RuleMessageSeverity.Error,
                        $"Column '{column}' was not found on '{table}'."));
                    hasErrors = true;
                    continue;
                }

                var result = RuleValidator.Validate(
                    rule, attr, new RuleValidationContext(table, recordCount, RunId));
                messages.AddRange(result.Messages);
                if (result.Messages.Any(m => m.Severity == RuleMessageSeverity.Error))
                    hasErrors = true;
                if (result.IsValid && result.EffectiveRule is not null)
                {
                    tableRules[column] = result.EffectiveRule;

                    var values = new List<string>(5);
                    if (result.EffectiveRule is LookupRandomRule)
                    {
                        values.Add(
                            $"Uses up to {LookupRandomRule.MaximumCandidatesPerTarget.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} existing records per target, captured before generation. Same seed and captured records give the same picks. Preview is resolved when the run starts. Candidate validation happens at Start before writes.");
                    }
                    else
                    {
                        var eval = new RuleEvaluationContext(table, Seed, Locale, RunId, recordCount);
                        using var session = result.EffectiveRule is BogusRule
                            ? new BogusEvaluatorSession(Locale)
                            : null;
                        PreparedBogusRule? prepared = result.EffectiveRule is BogusRule bogus
                            ? BogusRulePreparer.CompileRule(bogus, attr, eval)
                            : null;
                        for (var row = 0; row < 5; row++)
                        {
                            var value = prepared is not null && session is not null
                                ? session.Evaluate(prepared, attr, eval, row)
                                : RuleValueGenerator.Evaluate(result.EffectiveRule, attr, Seed, table, row, RunId);
                            values.Add(FormatPreview(value));
                        }
                    }

                    var displayName = attr.DisplayName?.UserLocalizedLabel?.Label ?? column;
                    previewRows.Add(new ReviewPreviewRow(table, column, displayName, values));
                }
            }

            if (tableRules.Count > 0)
                reviewed[table] = tableRules;
        }

        ReviewedRules = reviewed;
        ReviewMessages = messages;
        ReviewHasErrors = hasErrors;
        ReviewedDraftRevision = _fieldRules.Revision;
        ReviewPreviewRows = previewRows;
        IsReviewOpen = true;
    }

    /// <summary>
    /// Ensures <paramref name="logicalNames"/> are present in <see cref="EntityMetadataMap"/>,
    /// fetching only the ones not already cached and merging them in — additive, never removes
    /// or replaces entries <see cref="GoToRulesAsync"/> (or a prior call) already fetched. Lets a
    /// profiles-first flow (Profiles → Generate…) validate against real metadata without requiring
    /// a prior visit to the Rules step. Tables unavailable in this environment are skipped, so
    /// per-table import validation can report them; only when every requested fetch fails does
    /// the failure propagate to the caller rather than resolve to an empty map.
    /// </summary>
    /// <param name="logicalNames">Table logical names to ensure metadata for.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task EnsureMetadataAsync(IEnumerable<string> logicalNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(logicalNames);

        var missing = logicalNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && !_entityMetadata.ContainsKey(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length == 0)
            return;

        var results = await Task.WhenAll(missing.Select(n => FetchOrFailAsync(n, ct)));

        var failures = results.Where(r => r.Meta is null).ToArray();
        if (failures.Length == missing.Length)
            throw failures[0].Error!;

        foreach (var r in results.Where(r => r.Meta is not null))
            _entityMetadata[r.Meta!.LogicalName ?? r.Name] = r.Meta;

        if (failures.Length > 0)
        {
            _logger.LogWarning("Table(s) not available in this environment: {Tables}",
                string.Join(", ", failures.Select(f => f.Name)));
        }

        OnPropertyChanged(nameof(EntityMetadataMap));
    }

    private async Task<(string Name, Microsoft.Xrm.Sdk.Metadata.EntityMetadata? Meta, Exception? Error)>
        FetchOrFailAsync(
            string name, CancellationToken ct)
    {
        try
        {
            return (name, await _metadataProvider.GetEntityAsync(name, ct), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (name, null, ex);
        }
    }

    private bool CanGoToReview() => IsRulesLoaded && !IsRunning;

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

    /// <summary>
    /// Discards the draft and restores the previously committed configuration (S2).
    /// Never starts generation.
    /// </summary>
    [RelayCommand]
    private void CancelDraft()
    {
        _fieldRules?.DiscardDraft();
        CurrentStep = 0;
    }

    /// <summary>Tests set this to skip the content dialog.</summary>
    internal Func<Task<bool>>? ConfirmReset { get; set; }

    private async Task<bool> ConfirmHardResetAsync()
    {
        if (ConfirmReset is not null)
            return await ConfirmReset();
        return await _contentDialogService.ConfirmAsync(
            "Reset wizard",
            "Clear selected tables, rules, counts, seed, and the saved draft? This cannot be undone.",
            "Reset");
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        if (!await ConfirmHardResetAsync())
            return;
        await WipeWizardAsync();
    }

    /// <summary>Used by org-switch reload. No prompt.</summary>
    public Task ResetWithoutPromptAsync() => WipeWizardAsync();

    private async Task WipeWizardAsync()
    {
        await _draftAutosave.CancelPendingAsync();

        SelectedEntities = [];
        _entitySelector?.ClearSelection();
        CurrentProgress = null;
        LastResult = null;
        QueuedEntities.Clear();
        _fieldOverrides?.SetEntities([]);
        OnPropertyChanged(nameof(SelectedTableRows));

        _entityMetadata = new(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(EntityMetadataMap));
        IsRulesLoaded = false;
        CurrentStep = 0;
        ReviewedRules = null;
        ReviewMessages = [];
        ReviewHasErrors = false;
        ReviewedDraftRevision = null;
        ReviewPreviewRows = [];
        RunId = "";
        Seed = 42;
        ActiveProfileName = "No profile loaded";
        _fieldRules?.HardReset();

        _profileBridge.ResetSession();

        await _draftAutosave.CancelPendingAsync();

        await _profileBridge.ClearDraftAsync();

        try
        {
            var settings = await _settingsService.LoadAsync();
            BatchSize = settings.DefaultBatchSize;
            MaxParallelism = settings.DefaultDop;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Settings reload skipped");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartGenerate))]
    private async Task GenerateAsync()
    {
        IsRunning = true;
        CurrentProgress = null;
        LastResult = null;
        _cts = new CancellationTokenSource();
        _fieldRules?.Commit();
        // §07: Start promotes draft (Commit) and persists the promoted snapshot.
        await _draftAutosave.PersistAsync();
        var config = BuildConfig();
        // WR-002: captured before the run — the History row must not depend on later wizard state.
        var tableNames = SelectedEntities.Select(e => e.DisplayName).ToArray();
        Exception? failure = null;
        try
        {
            var names = SelectedEntities.Select(e => e.LogicalName).ToArray();
            LastResult = await Run.ExecuteAsync(config, string.Empty, names, PlannedTotal, _cts.Token);
            if (LastResult.Cancelled)
                Run.ReportRunFailure(new OperationCanceledException());
            else
                ReportOutcome(LastResult);
            if (!Run.KeepWindowOpen)
                _navigator?.Navigate(typeof(RunSummaryPage));
        }
        catch (Exception ex)
        {
            failure = ex;
            Run.ReportRunFailure(ex);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }

        // WR-002: one History write for every run that returned a result or failed. A cancel
        // that throws (including a declined risky-value prompt) wrote nothing, so it has no row.
        if (LastResult is not null || failure is not (null or OperationCanceledException))
            await RecordRunAsync(tableNames, LastResult);
    }

    private async Task RecordRunAsync(string[] tableNames, GenerationResult? result)
    {
        try
        {
            await _historyService.AddRunAsync(new RunRecord(
                Run.CurrentRunId,
                DateTimeOffset.Now,
                tableNames,
                result?.TotalRecords ?? 0,
                result?.Elapsed ?? TimeSpan.Zero,
                result is { Cancelled: false, Errors.Count: 0 },
                result?.Errors.Sum(e => e.RowCount) ?? 0,
                Run.EnvironmentLabel,
                Run.UserLabel,
                ActiveProfileName,
                Run.ActivityLines));
        }
        catch (Exception ex)
        {
            // A History failure is not a generation failure — report it on its own.
            _logger.LogError(ex, "Failed to record the run in history");
            _snackbar.Show("Couldn't save run history", ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    private GenerationConfig BuildConfig()
    {
        var rawCounts = _fieldOverrides?.GetCounts() ?? new Dictionary<string, int>();
        return new GenerationConfig
        {
            EntityLogicalNames = [.. SelectedEntities.Select(e => e.LogicalName)],
            RecordCounts = SelectedEntities.ToDictionary(
                e => e.LogicalName,
                e => rawCounts.GetValueOrDefault(e.LogicalName, DefaultRecordCount)),
            Seed = Seed,
            Locale = Locale,
            BatchSize = BatchSize,
            MaxParallelism = MaxParallelism == 0 ? null : MaxParallelism,
            FieldRules = ReviewedRules is { Count: > 0 } ? ReviewedRules : null,
            RunId = RunId,
        };
    }

    private void ReportOutcome(GenerationResult result) =>
        _snackbar.Show(
            result.Errors.Count == 0 ? "Success" : "Completed with errors",
            result.Errors.Count == 0
                ? $"Created {result.TotalRecords:N0} records"
                : $"Created {result.TotalRecords:N0} records with {result.Errors.Count} error(s)",
            result.Errors.Count == 0
                ? Wpf.Ui.Controls.ControlAppearance.Success
                : Wpf.Ui.Controls.ControlAppearance.Caution,
            null,
            TimeSpan.FromSeconds(result.Errors.Count == 0 ? 3 : 5));

    private bool CanStartGenerate() =>
        !IsRunning
        && SelectedEntities.Count > 0
        && ReviewedRules is not null
        && !ReviewHasErrors
        && _fieldRules is not null
        && ReviewedDraftRevision == _fieldRules.Revision;

    private bool CanReset() => !IsRunning;

    /// <summary>Opens <see cref="RulesPage"/> with an in-memory working-set snapshot. Does not persist.</summary>
    [RelayCommand]
    private void EditRules()
    {
        if (_navigator is null || !_profileBridge.TryPrepareRulesEdit())
            return;

        _navigator.Navigate(typeof(RulesPage));
    }

    /// <summary>Navigates to <see cref="ProfilesPage"/> to browse/load/manage profiles.</summary>
    [RelayCommand]
    private void OpenProfiles() => _navigator?.Navigate(typeof(ProfilesPage));

    /// <summary>Builds a profile snapshot of the current wizard selection, counts, rules, and seed.</summary>
    public Profile BuildProfileSnapshot(string name) => _profileBridge.BuildProfileSnapshot(name);

    /// <summary>Pushes a metadata-validated import report onto the board (EffectiveRules only).</summary>
    public void ApplyImportReport(ProfileImportReport report) => _profileBridge.ApplyImportReport(report);
}
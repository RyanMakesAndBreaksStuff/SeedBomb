using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Contracts;
using DataGen.Core.Generators;
using DataGen.Core.Metadata;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk;
using Seedbomb.ViewModels.Controls;
using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Extensions;
using Seedbomb.Services.Generation;
using Seedbomb.Services.History;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Profiles;
using Seedbomb.Services.Settings;
using Seedbomb.ViewModels;
using Seedbomb.Views.Dialogs;
using Seedbomb.Views.Pages;

namespace Seedbomb.ViewModels;

/// <summary>Step entry in the horizontal stepper strip above the wizard cards.</summary>
/// <param name="Glyph">Displayed circle glyph.</param>
/// <param name="Label">Displayed step label.</param>
/// <param name="IsDone">Whether the step has been completed.</param>
/// <param name="IsActive">Whether the step is the current active step.</param>
/// <param name="HasNext">Whether a connector line should be drawn after this step.</param>
public record StepEntry(string Glyph, string Label, bool IsDone, bool IsActive, bool HasNext);

/// <summary>One selected table in the step-1 aside.</summary>
public sealed record SelectedTableRow(string DisplayName, string LogicalName, int Count);

/// <summary>Entity queue status entry shown in the right-side queue dot list.</summary>
public sealed partial class QueuedEntityEntry : ObservableObject
{
    /// <summary>Gets the entity summary.</summary>
    public EntitySummary Entity { get; }

    [ObservableProperty]
    private System.Windows.Media.Brush _dotBrush =
        System.Windows.Media.Brushes.LightGray;

    /// <summary>Initialises the entry.</summary>
    /// <param name="entity">The entity.</param>
    public QueuedEntityEntry(EntitySummary entity) => Entity = entity;
}

/// <summary>Review-card preview: first five <see cref="RuleValueGenerator"/> outputs for one ruled column.</summary>
/// <param name="Table">Owning table logical name.</param>
/// <param name="Column">Column logical name.</param>
/// <param name="DisplayName">Column display name.</param>
/// <param name="Values">Up to five formatted preview values (row 0..4).</param>
public sealed record ReviewPreviewRow(
    string Table, string Column, string DisplayName, IReadOnlyList<string> Values)
{
    /// <summary>Values joined for the 828px review row.</summary>
    public string SampleLine => string.Join(" · ", Values);
}

/// <summary>ViewModel for the Generate wizard page.</summary>
public sealed partial class GenerateViewModel : ViewModelBase
{
    private readonly IRunHistoryService _historyService;
    private readonly ISettingsService _settingsService;
    private readonly ISnackbarService _snackbar;
    private readonly IMetadataProvider _metadataProvider;
    private readonly IProfileService _profileService;
    private readonly IContentDialogService _contentDialogService;
    private readonly ILogger<GenerateViewModel> _logger;
    private readonly RulesNavigationRequest? _rulesRequest;
    private readonly IAppNavigator? _navigator;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _rulesCts;
    private readonly HashSet<string> _queueCompletedEntities = new(StringComparer.OrdinalIgnoreCase);
    private string? _queueCurrentEntity;
    private CancellationTokenSource? _draftSaveCts;
    private Task? _draftSaveTask;
    private Profile? _restoredDraft;
    private FieldOverridesViewModel? _fieldOverrides;
    private FieldRulesViewModel _fieldRules;
    private Dictionary<string, Microsoft.Xrm.Sdk.Metadata.EntityMetadata> _entityMetadata =
        new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTableRows), nameof(PlannedTotal))]
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
        IAppNavigator? navigator = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        _historyService = historyService;
        _settingsService = settingsService;
        _snackbar = snackbar;
        _logger = logger;
        _metadataProvider = metadataProvider;
        _profileService = profileService;
        _contentDialogService = contentDialogService;
        _rulesRequest = rulesRequest;
        _navigator = navigator;
        Run = run;
        _fieldRules = new FieldRulesViewModel();
        AttachFieldRules(_fieldRules);
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

            try
            {
                _restoredDraft = await _profileService.LoadDraftAsync();
                if (_restoredDraft?.Seed is int seed)
                    Seed = seed;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Draft profile load skipped");
                _restoredDraft = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise the Generate page");
            _snackbar.Show("Couldn't load the Generate page", ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    /// <inheritdoc />
    public override Task OnNavigatedFromAsync()
    {
        if (_rulesRequest is not null && SelectedEntities.Count > 0)
        {
            _rulesRequest.Profile = BuildProfileSnapshot(
                string.Equals(ActiveProfileName, "No profile loaded", StringComparison.Ordinal)
                    ? "working-set"
                    : ActiveProfileName);
            _rulesRequest.TableName = SelectedEntities[0].LogicalName;
            _rulesRequest.OnSaved = ApplySavedRulesProfile;
            _rulesRequest.ReturnPage = typeof(GeneratePage);
        }

        return Task.CompletedTask;
    }

    // ── State ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(CanConfigure),
        nameof(CanExecute),
        nameof(HasEntities),
        nameof(SelectedEntitiesSummary),
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
    [NotifyPropertyChangedFor(nameof(IsGenerating), nameof(GenerateLabel), nameof(StatusLabel), nameof(Steps), nameof(HasStarted))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToReviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    private bool _isCancelling;

    [ObservableProperty]
    private ProgressUpdate? _currentProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasResult), nameof(StatusLabel), nameof(Steps), nameof(HasStarted),
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
    [NotifyPropertyChangedFor(nameof(Steps), nameof(ReviewedRuleCount), nameof(RulesLinkLabel), nameof(RunPlanStats), nameof(ProfileSummaryLine))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private Dictionary<string, Dictionary<string, FieldRule>>? _reviewedRules;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReviewErrorSummary))]
    private IReadOnlyList<RuleMessage> _reviewMessages = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _reviewHasErrors;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private long? _reviewedDraftRevision;

    [ObservableProperty] private string _runId = "";

    [ObservableProperty] private IReadOnlyList<ReviewPreviewRow> _reviewPreviewRows = [];

    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>Gets a value indicating whether step 2 (configure) should be visible.</summary>
    public bool CanConfigure => SelectedEntities.Count > 0;

    /// <summary>Gets a value indicating whether step 3 (execute) should be visible.</summary>
    public bool CanExecute => CanConfigure;

    /// <summary>Gets a value indicating whether entities have been selected.</summary>
    public bool HasEntities => SelectedEntities.Count > 0;

    /// <summary>Gets a compact selected-entity summary for step card headers.</summary>
    public string SelectedEntitiesSummary =>
        SelectedEntities.Count == 0
            ? "Waiting"
            : string.Join(" · ", SelectedEntities.Take(4).Select(e => e.DisplayName)) +
              (SelectedEntities.Count > 4 ? $" · {SelectedEntities.Count} entities" : string.Empty);

    /// <summary>Owned field-rules draft. Tests may swap via <see cref="AttachFieldRules"/>.</summary>
    public FieldRulesViewModel FieldRules => _fieldRules;

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
    public string RulesLinkLabel => $"{ReviewedRuleCount} rules · edit";

    /// <summary>Handoff alias of <see cref="ReviewPreviewRows"/>.</summary>
    public IReadOnlyList<ReviewPreviewRow> ReviewedPreviewRows => ReviewPreviewRows;

    /// <summary>Joined preflight errors for the review banner.</summary>
    public string ReviewErrorSummary =>
        string.Join(
            Environment.NewLine,
            ReviewMessages.Where(m => m.Severity == RuleMessageSeverity.Error).Select(m => m.Text));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileSummaryLine))]
    private string _activeProfileName = "No profile loaded";

    /// <summary>Step-1 SELECTED rows. Counts come from FieldOverrides when attached, else <see cref="DefaultRecordCount"/>.</summary>
    public IReadOnlyList<SelectedTableRow> SelectedTableRows
    {
        get
        {
            var counts = _fieldOverrides?.GetCounts();
            return [.. SelectedEntities.Select(e => new SelectedTableRow(
                e.DisplayName,
                e.LogicalName,
                counts is not null && counts.TryGetValue(e.LogicalName, out var n) ? n : DefaultRecordCount))];
        }
    }

    /// <summary>PROFILE card second line.</summary>
    public string ProfileSummaryLine =>
        $"{ReviewedRuleCount} rules · seed {Seed} · {Locale}";

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

    /// <summary>Step 4 confirmation sentence.</summary>
    public string RunConfirmationLine =>
        $"Write {PlannedTotal:N0} rows across {SelectedEntities.Count} table(s) using seed {Seed}. Nothing is written until you start.";

    /// <summary>Step 4 stat tiles.</summary>
    public IReadOnlyList<RunValueRow> RunPlanStats =>
    [
        new("Tables", SelectedEntities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Rows", PlannedTotal.ToString("N0")),
        new("Rules", ReviewedRuleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Seed", Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("Locale", Locale),
    ];

    /// <summary>Gets a value indicating whether a generation run is in progress.</summary>
    public bool IsGenerating => IsRunning;

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

    /// <summary>Gets a value indicating whether generation has started (running or complete) — gates the Execute status card.</summary>
    public bool HasStarted => IsRunning || HasResult;

    /// <summary>Gets the label for the Generate button.</summary>
    public string GenerateLabel => IsRunning ? "GENERATING…" : "GENERATE DATA";

    /// <summary>Gets the label for the Abort button.</summary>
    public string CancelLabel => IsCancelling ? "ABORTING…" : "ABORT";

    /// <summary>Gets the status panel label.</summary>
    public string StatusLabel => IsRunning ? "In progress" : (LastResult is null ? "Ready" : "Complete");

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
    /// <see cref="Seedbomb.Views.Controls.FieldOverridesControl"/> so counts can be
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
        ScheduleDraftAutosave();
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
        _queueCompletedEntities.Clear();
        _queueCurrentEntity = null;
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

    partial void OnCurrentProgressChanged(ProgressUpdate? value)
    {
        if (value is null || string.IsNullOrEmpty(value.EntityName))
            return;

        if (_queueCurrentEntity is not null
            && !string.Equals(_queueCurrentEntity, value.EntityName, StringComparison.OrdinalIgnoreCase))
        {
            _queueCompletedEntities.Add(_queueCurrentEntity);
        }

        _queueCurrentEntity = value.EntityName;
        ApplyQueueDots();
    }

    partial void OnLastResultChanged(GenerationResult? value) => ApplyQueueDots();

    private void ApplyQueueDots()
    {
        foreach (var entry in QueuedEntities)
        {
            var name = entry.Entity.LogicalName;
            if (LastResult?.Errors.Any(err =>
                    string.Equals(err.EntityLogicalName, name, StringComparison.OrdinalIgnoreCase)) == true)
            {
                entry.DotBrush = ThemeBrush("DG.Error", Brushes.IndianRed);
            }
            else if (LastResult?.CreatedRecords.ContainsKey(name) == true
                     || _queueCompletedEntities.Contains(name))
            {
                entry.DotBrush = ThemeBrush("DG.Success", Brushes.ForestGreen);
            }
            else if (string.Equals(name, CurrentProgress?.EntityName, StringComparison.OrdinalIgnoreCase))
            {
                entry.DotBrush = ThemeBrush("DG.Accent", Brushes.DodgerBlue);
            }
            else
            {
                entry.DotBrush = Brushes.LightGray;
            }
        }
    }

    private static Brush ThemeBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

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
            TryApplyRestoredDraft();
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
    /// a prior visit to the Rules step. Provider failures propagate to the caller rather than
    /// being swallowed into an empty map.
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

        var list = await _metadataProvider.GetEntitiesAsync(missing, ct);
        foreach (var m in list.Where(m => m.LogicalName is not null))
            _entityMetadata[m.LogicalName!] = m;

        OnPropertyChanged(nameof(EntityMetadataMap));
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
        var result = await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Reset wizard",
            Content = "Clear selected tables, rules, counts, seed, and the saved draft? This cannot be undone.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
        });
        return result == Wpf.Ui.Controls.ContentDialogResult.Primary;
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
        await CancelPendingAutosaveAsync();

        SelectedEntities = [];
        CurrentProgress = null;
        LastResult = null;
        QueuedEntities.Clear();
        _queueCompletedEntities.Clear();
        _queueCurrentEntity = null;
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
        _restoredDraft = null;
        _fieldRules?.HardReset();

        _rulesRequest?.Clear();

        await CancelPendingAutosaveAsync();

        try
        {
            await _profileService.ClearDraftAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Draft clear skipped");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartGenerate))]
    private async Task GenerateAsync()
    {
        IsRunning = true;
        CurrentProgress = null;
        LastResult = null;
        IsCancelling = false;
        _cts = new CancellationTokenSource();

        _fieldRules?.Commit();
        // §07: Start promotes draft (Commit) and persists the promoted snapshot.
        _ = PersistDraftAsync();

        var rawCounts = _fieldOverrides?.GetCounts() ?? new Dictionary<string, int>();
        var config = new GenerationConfig
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

        try
        {
            var names = SelectedEntities.Select(e => e.LogicalName).ToArray();
            var host = "Dataverse";
            LastResult = await Run.ExecuteAsync(config, host, names, PlannedTotal, _cts.Token);
            var result = LastResult!;

            await _historyService.AddRunAsync(new RunRecord(
                Run.CurrentRunId,
                DateTimeOffset.Now,
                SelectedEntities.Select(e => e.DisplayName).ToArray(),
                result.TotalRecords,
                result.Elapsed,
                result.Errors.Count == 0,
                result.Errors.Count));

            if (result.Errors.Count == 0)
            {
                _snackbar.Show(
                    "Success",
                    $"Created {result.TotalRecords:N0} records",
                    Wpf.Ui.Controls.ControlAppearance.Success,
                    null,
                    TimeSpan.FromSeconds(3));
            }
            else
            {
                _snackbar.Show(
                    "Completed with errors",
                    $"Created {result.TotalRecords:N0} records with {result.Errors.Count} error(s)",
                    Wpf.Ui.Controls.ControlAppearance.Caution,
                    null,
                    TimeSpan.FromSeconds(5));
            }

            if (!Run.KeepWindowOpen)
                _navigator?.Navigate(typeof(RunSummaryPage));
        }
        catch (OperationCanceledException)
        {
            _snackbar.Show("Cancelled", "Generation cancelled",
                Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
        }
        catch (MsalUiRequiredException)
        {
            _snackbar.Show("Session expired", "Please sign in again",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _snackbar.Show("Error", "Generation failed — see logs for details",
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
            _logger.LogError(ex, "Generation failed");
        }
        finally
        {
            IsRunning = false;
            IsCancelling = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

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
        if (_rulesRequest is null || _navigator is null)
            return;

        _rulesRequest.Profile = BuildProfileSnapshot("working-set");
        _rulesRequest.TableName = SelectedEntities.FirstOrDefault()?.LogicalName;
        _rulesRequest.OnSaved = ApplySavedRulesProfile;
        _rulesRequest.ReturnPage = typeof(GeneratePage);
        _navigator.Navigate(typeof(RulesPage));
    }

    private void ApplySavedRulesProfile(Profile profile)
    {
        ActiveProfileName = profile.Name;
        if (_entityMetadata.Count > 0)
        {
            var report = ProfileImport.ValidateAgainstMetadata(profile, _entityMetadata, RunId);
            ApplyImportReport(report);
            return;
        }

        if (profile.Seed is int seed)
            Seed = seed;

        foreach (var table in profile.Tables)
            _fieldOverrides?.SetCount(table.Table, table.Count);

        var draft = new Dictionary<string, Dictionary<string, RuleDraftEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in profile.Tables)
        {
            if (table.Columns is null || table.Columns.Count == 0)
                continue;

            var cols = new Dictionary<string, RuleDraftEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, rule) in table.Columns)
                cols[column] = new RuleDraftEntry(rule, column, "");
            draft[table.Table] = cols;
        }

        _fieldRules.ReplaceDraft(draft);
        if (SelectedEntities.Count > 0)
            _fieldRules.SelectTable(SelectedEntities[0].LogicalName);
    }

    /// <summary>Opens the Profiles manager dialog (Mock F5) and applies Open-in-board results.</summary>
    [RelayCommand]
    private async Task OpenProfilesAsync()
    {
        var vm = new ProfilesViewModel(_profileService)
        {
            CaptureCurrent = name => BuildProfileSnapshot(name),
            IsBoardDirty = () => _fieldRules?.IsDirty == true,
            GetMetadata = () => _entityMetadata,
            GetRunId = () => RunId,
            ConfirmOverwrite = msg =>
                System.Windows.MessageBox.Show(msg, "Load profile",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
            ConfirmDelete = name =>
                System.Windows.MessageBox.Show(
                    $"Delete profile '{name}'? This cannot be undone.",
                    "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
            PickImportPath = () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Profile (*.profile.json)|*.profile.json|JSON (*.json)|*.json|All files|*.*",
                    Title = "Import profile",
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            },
            PickExportPath = name =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Profile (*.profile.json)|*.profile.json",
                    FileName = $"{name}.profile.json",
                    Title = "Export profile",
                };
                return dlg.ShowDialog() == true ? dlg.FileName : null;
            },
        };

        await vm.RefreshCommand.ExecuteAsync(null);

        var dialog = new ProfilesDialog(vm);
        await _contentDialogService.ShowAsync(dialog, CancellationToken.None);

        if (vm.AppliedToBoard && vm.PendingImport is { } report)
            ApplyImportReport(report);
    }

    /// <summary>Builds a profile snapshot of the current wizard selection, counts, rules, and seed.</summary>
    public Profile BuildProfileSnapshot(string name)
    {
        var counts = _fieldOverrides?.GetCounts() ?? new Dictionary<string, int>();
        var rules = _fieldRules?.GetRules()
            ?? new Dictionary<string, Dictionary<string, FieldRule>>(StringComparer.OrdinalIgnoreCase);

        var tables = new List<ProfileTable>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in SelectedEntities)
        {
            seen.Add(entity.LogicalName);
            Dictionary<string, FieldRule>? cols = null;
            if (rules.TryGetValue(entity.LogicalName, out var r) && r.Count > 0)
                cols = new Dictionary<string, FieldRule>(r, StringComparer.OrdinalIgnoreCase);
            tables.Add(new ProfileTable(
                entity.LogicalName,
                counts.GetValueOrDefault(entity.LogicalName, DefaultRecordCount),
                cols));
        }

        foreach (var (table, cols) in rules)
        {
            if (seen.Contains(table) || cols.Count == 0) continue;
            tables.Add(new ProfileTable(
                table,
                counts.GetValueOrDefault(table, DefaultRecordCount),
                new Dictionary<string, FieldRule>(cols, StringComparer.OrdinalIgnoreCase)));
        }

        if (tables.Count == 0)
            tables.Add(new ProfileTable("account", DefaultRecordCount, null));

        return new Profile(1, name, Description: null, Seed, tables);
    }

    /// <summary>Pushes a metadata-validated import report onto the board (EffectiveRules only).</summary>
    public void ApplyImportReport(ProfileImportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Seed is int seed)
            Seed = seed;

        foreach (var (table, count) in report.TableCounts)
            _fieldOverrides?.SetCount(table, count);

        if (_fieldRules is null)
            return;

        var draft = new Dictionary<string, Dictionary<string, RuleDraftEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, columns) in report.BoardRules)
        {
            _entityMetadata.TryGetValue(table, out var meta);
            var attrs = (meta?.Attributes ?? [])
                .Where(a => a.LogicalName is not null)
                .ToDictionary(a => a.LogicalName!, StringComparer.OrdinalIgnoreCase);

            var tableDraft = new Dictionary<string, RuleDraftEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, rule) in columns)
            {
                var display = column;
                if (attrs.TryGetValue(column, out var attr))
                    display = attr.DisplayName?.UserLocalizedLabel?.Label ?? column;
                tableDraft[column] = new RuleDraftEntry(rule, display, "");
            }

            if (tableDraft.Count > 0)
                draft[table] = tableDraft;
        }

        _fieldRules.ReplaceDraft(draft);
        if (SelectedEntities.Count > 0)
            _fieldRules.SelectTable(SelectedEntities[0].LogicalName);
    }

    private void TryApplyRestoredDraft()
    {
        if (_restoredDraft is null || _fieldRules is null || _entityMetadata.Count == 0)
            return;

        var hasTables = _restoredDraft.Tables.Count > 0;
        var report = ProfileImport.ValidateAgainstMetadata(_restoredDraft, _entityMetadata, RunId);
        ApplyImportReport(report);
        _restoredDraft = null;
        if (hasTables)
            CurrentStep = 1;
    }

    private void ScheduleDraftAutosave()
    {
        _draftSaveCts?.Cancel();
        _draftSaveCts?.Dispose();
        _draftSaveCts = new CancellationTokenSource();
        _draftSaveTask = DebouncedSaveDraftAsync(_draftSaveCts.Token);
    }

    private async Task CancelPendingAutosaveAsync()
    {
        _draftSaveCts?.Cancel();
        if (_draftSaveTask is { } pending)
        {
            try { await pending; }
            catch (OperationCanceledException) { }
        }

        _draftSaveCts?.Dispose();
        _draftSaveCts = null;
        _draftSaveTask = null;
    }

    private async Task DebouncedSaveDraftAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
        }
        catch (OperationCanceledException)
        {
            return; // coalesced by a newer edit
        }

        await PersistDraftAsync(ct);
    }

    // WR-006: GenerateAsync calls this as `_ = PersistDraftAsync()`, bypassing the
    // debounce wrapper. The handler has to live here or that call is unobserved.
    private async Task PersistDraftAsync(CancellationToken ct = default)
    {
        if (SelectedEntities.Count == 0 && (_fieldRules is null || _fieldRules.GetRules().Count == 0))
            return;

        try
        {
            var profile = BuildProfileSnapshot("draft");
            await _profileService.SaveDraftAsync(profile, ct);
        }
        catch (OperationCanceledException)
        {
            // coalesced
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Draft autosave failed");
        }
    }
}

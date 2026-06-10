using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Contracts;
using DataGen.Desktop.Services.Generation;
using DataGen.Desktop.Services.History;
using DataGen.Desktop.ViewModels.Controls;
using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;
using Wpf.Ui;

namespace DataGen.Desktop.ViewModels;

/// <summary>Step entry in the horizontal stepper strip above the wizard cards.</summary>
/// <param name="Glyph">Displayed circle glyph.</param>
/// <param name="Label">Displayed step label.</param>
/// <param name="IsDone">Whether the step has been completed.</param>
/// <param name="IsActive">Whether the step is the current active step.</param>
/// <param name="HasNext">Whether a connector line should be drawn after this step.</param>
public record StepEntry(string Glyph, string Label, bool IsDone, bool IsActive, bool HasNext);

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

/// <summary>ViewModel for the Generate wizard page.</summary>
public sealed partial class GenerateViewModel : ViewModelBase
{
    private readonly IWpfGenerationService _generationService;
    private readonly IRunHistoryService _historyService;
    private readonly ISnackbarService _snackbar;
    private readonly ILogger<GenerateViewModel> _logger;

    private CancellationTokenSource? _cts;
    private FieldOverridesViewModel? _fieldOverrides;

    /// <summary>Initialises the view-model.</summary>
    /// <param name="generationService">Generation pipeline service.</param>
    /// <param name="historyService">Run history persistence service.</param>
    /// <param name="snackbar">Snackbar notification service.</param>
    /// <param name="logger">Logger.</param>
    public GenerateViewModel(
        IWpfGenerationService generationService,
        IRunHistoryService historyService,
        ISnackbarService snackbar,
        ILogger<GenerateViewModel> logger)
    {
        _generationService = generationService;
        _historyService = historyService;
        _snackbar = snackbar;
        _logger = logger;
    }

    // ── State ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(CanConfigure),
        nameof(CanExecute),
        nameof(HasEntities),
        nameof(SelectedEntitiesSummary),
        nameof(Steps))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private IReadOnlyList<EntitySummary> _selectedEntities = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGenerating), nameof(GenerateLabel), nameof(StatusLabel), nameof(Steps))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    private bool _isCancelling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private ProgressUpdate? _currentProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(StatusLabel), nameof(Steps))]
    private GenerationResult? _lastResult;

    [ObservableProperty] private int _seed = 42;
    [ObservableProperty] private int _batchSize = 500;
    [ObservableProperty] private int _maxParallelism;

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

    /// <summary>Gets a value indicating whether a generation run is in progress.</summary>
    public bool IsGenerating => IsRunning;

    /// <summary>Gets a value indicating whether live progress is available.</summary>
    public bool HasProgress => CurrentProgress is not null;

    /// <summary>Gets a value indicating whether a result is available to display.</summary>
    public bool HasResult => LastResult is not null;

    /// <summary>Gets the label for the Generate button.</summary>
    public string GenerateLabel => IsRunning ? "GENERATING…" : "GENERATE DATA";

    /// <summary>Gets the label for the Abort button.</summary>
    public string CancelLabel => IsCancelling ? "ABORTING…" : "ABORT";

    /// <summary>Gets the status panel label.</summary>
    public string StatusLabel => IsRunning ? "In progress" : (LastResult is null ? "Ready" : "Complete");

    /// <summary>Gets the collection of queued entity status dots.</summary>
    public ObservableCollection<QueuedEntityEntry> QueuedEntities { get; } = [];

    /// <summary>Gets the current stepper step entries.</summary>
    public IReadOnlyList<StepEntry> Steps =>
    [
        new(CanConfigure ? "✓" : "1", "Select", CanConfigure, !CanConfigure, true),
        new(CanExecute && CanConfigure ? "✓" : "2", "Configure", CanExecute && CanConfigure, CanConfigure && !CanExecute, true),
        new(HasResult ? "✓" : "3", "Execute", HasResult, CanExecute && !HasResult, false),
    ];

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches the <see cref="FieldOverridesViewModel"/> instance owned by the page's
    /// <see cref="DataGen.Desktop.Views.Controls.FieldOverridesControl"/> so counts can be
    /// read at generation time.
    /// </summary>
    /// <param name="vm">The FieldOverrides view-model.</param>
    public void AttachFieldOverrides(FieldOverridesViewModel vm) => _fieldOverrides = vm;

    /// <summary>Called by the page when entity selection changes.</summary>
    /// <param name="entities">Newly selected entities.</param>
    public void OnEntitiesChanged(IReadOnlyList<EntitySummary> entities)
    {
        SelectedEntities = entities;
        _fieldOverrides?.SetEntities(entities);

        QueuedEntities.Clear();
        foreach (var e in entities)
            QueuedEntities.Add(new QueuedEntityEntry(e));
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset()
    {
        SelectedEntities = [];
        CurrentProgress = null;
        LastResult = null;
        QueuedEntities.Clear();
        _fieldOverrides?.SetEntities([]);
    }

    [RelayCommand(CanExecute = nameof(CanStartGenerate))]
    private async Task GenerateAsync()
    {
        IsRunning = true;
        CurrentProgress = null;
        LastResult = null;
        IsCancelling = false;
        _cts = new CancellationTokenSource();

        var rawCounts = _fieldOverrides?.GetCounts() ?? new Dictionary<string, int>();
        var config = new GenerationConfig
        {
            EntityLogicalNames = [.. SelectedEntities.Select(e => e.LogicalName)],
            RecordCounts = SelectedEntities.ToDictionary(
                e => e.LogicalName,
                e => rawCounts.GetValueOrDefault(e.LogicalName, 10)),
            Seed = Seed,
            BatchSize = BatchSize,
            MaxParallelism = MaxParallelism == 0 ? null : MaxParallelism,
        };

        var progress = new Progress<ProgressUpdate>(update =>
            Application.Current.Dispatcher.InvokeAsync(() => CurrentProgress = update));

        try
        {
            var result = await _generationService.GenerateAsync(config, progress, _cts.Token);
            LastResult = result;

            await _historyService.AddRunAsync(new RunRecord(
                Guid.NewGuid(),
                DateTimeOffset.Now,
                SelectedEntities.Select(e => e.DisplayName).ToArray(),
                result.TotalRecords,
                result.Elapsed,
                result.Errors.Count == 0,
                result.Errors.Count));

            _snackbar.Show("Success", $"Created {result.TotalRecords:N0} records",
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
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

    private bool CanStartGenerate() => !IsRunning && SelectedEntities.Count > 0;

    private bool CanReset() => !IsRunning;

    /// <summary>Cancels a running generation.</summary>
    [RelayCommand]
    private void Abort()
    {
        IsCancelling = true;
        _cts?.Cancel();
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Contracts;
using DataGen.Core.Rules;
using Seedbomb.Services.Generation;
using Seedbomb.Services.History;
using Seedbomb.Services.Navigation;
using Seedbomb.Services.Settings;
using Seedbomb.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace Seedbomb.ViewModels;

/// <summary>Label/value row for sheet metrics and summary tiles. <see cref="ValueKind"/> maps to DG.* — no Brush.</summary>
public sealed record RunValueRow(string Label, string Value, string? ValueKind = null);

/// <summary>Per-table progress row. <see cref="StateKey"/> maps to DG.* — no Brush.</summary>
public sealed record RunTableProgressRow(
    string TableName,
    double Percent,
    string ProgressLabel,
    string StateLabel,
    string StateKey,
    string NameWeight);

/// <summary>One activity-log line. <see cref="ValueKind"/> maps to DG.* — no Brush.</summary>
public sealed record RunActivityRow(string Line, string? ValueKind = null);

/// <summary>Drives the in-progress sheet and the run summary. Owns first-run and retry calls to <see cref="IWpfGenerationService"/>.</summary>
public sealed partial class RunViewModel : ObservableObject
{
    private const string AllTablesFilter = "All tables";
    private const int ActivityLogCap = 200;
    private const int RecentActivityCap = 4;

    private readonly IWpfGenerationService? _generation;
    private readonly IContentDialogService? _dialogs;
    private readonly ISettingsService? _settings;
    private readonly IAppNavigator? _navigator;

    private readonly List<RejectionGroup> _allRejectionGroups = [];
    private readonly List<RunActivityRow> _activityLog = [];
    private readonly Dictionary<string, int> _tableWritten = new(StringComparer.OrdinalIgnoreCase);

    private GenerationConfig? _lastConfig;
    private CancellationTokenSource? _runCts;
    private IReadOnlyList<string> _plannedTables = [];
    private int _plannedTotal;
    private int _seed;
    private string _environmentHost = "";
    private bool _suppressKeepPersist;

    /// <summary>Tests set this to skip the risky-Bogus content dialog.</summary>
    internal Func<Task<bool>>? ConfirmRiskyBogus { get; set; }

    /// <summary>Optional services so grouping tests can <c>new RunViewModel()</c> and retry can pass a mock.</summary>
    /// <param name="generation">Pipeline used for first run and retry. Null disables retry.</param>
    /// <param name="contentDialogService">Cancel and log dialogs. Null skips them.</param>
    /// <param name="settings">Persists <see cref="KeepWindowOpen"/>.</param>
    /// <param name="navigator">Used by <see cref="OpenInHistory"/>.</param>
    public RunViewModel(
        IWpfGenerationService? generation = null,
        IContentDialogService? contentDialogService = null,
        ISettingsService? settings = null,
        IAppNavigator? navigator = null)
    {
        _generation = generation;
        _dialogs = contentDialogService;
        _settings = settings;
        _navigator = navigator;
    }

    /// <summary>Id of the live run. History uses this to reopen the live summary.</summary>
    public Guid CurrentRunId { get; private set; }

    /// <summary>True while generation is in flight. Drives the ring, headline, and Cancel/Close swap.</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>Drives the overlay's visibility. Stays true after finish when <see cref="KeepWindowOpen"/> is set,
    /// so the completed sheet holds until the user clicks Close.</summary>
    [ObservableProperty] private bool _isSheetVisible;

    /// <summary>True before the first batch completes.</summary>
    [ObservableProperty] private bool _isIndeterminate;

    /// <summary>0–100 overall progress for the ring.</summary>
    [ObservableProperty] private double _overallPercent;

    /// <summary>Percent digits shown inside the ring (no % suffix).</summary>
    [ObservableProperty] private string _overallPercentLabel = "0";

    /// <summary>Formatted rows-written count inside the ring.</summary>
    [ObservableProperty] private string _rowsWrittenLabel = "0";

    /// <summary>Generating… / Cancelling… / Cancelled.</summary>
    [ObservableProperty] private string _statusHeadline = "Generating…";

    /// <summary>Rows written, host, rules, seed.</summary>
    [ObservableProperty] private string _runDescription = "";

    /// <summary>Finished timestamp, duration, seed.</summary>
    [ObservableProperty] private string _runMetaLine = "";

    /// <summary>Resource key for the outcome banner style.</summary>
    [ObservableProperty] private string _outcomeBannerStyle = "DG.InfoBanner";

    /// <summary>Outcome glyph. Enum, not a Brush.</summary>
    [ObservableProperty] private SymbolRegular _outcomeGlyph = SymbolRegular.CheckmarkCircle24;

    /// <summary>Completed / completed-with-rejections headline.</summary>
    [ObservableProperty] private string _outcomeHeadline = "";

    /// <summary>Must contain "Nothing was rolled back".</summary>
    [ObservableProperty] private string _outcomeDetail = "";

    /// <summary>Retry N selected.</summary>
    [ObservableProperty] private string _retryButtonLabel = "Retry 0 selected";

    /// <summary>Cause/row/selection totals under the rejection list.</summary>
    [ObservableProperty] private string _rejectionFooterLabel = "0 causes · 0 rows · 0 selected for retry";

    /// <summary>Table filter for the rejection list.</summary>
    [ObservableProperty] private string _selectedTableFilter = AllTablesFilter;

    /// <summary>When true the list shows only retryable groups.</summary>
    [ObservableProperty] private bool _retryableOnly;

    /// <summary>Persisted via <see cref="AppSettings.KeepRunSheetOpen"/>. Defaults true.</summary>
    [ObservableProperty] private bool _keepWindowOpen = true;

    /// <summary>Elapsed / remaining / throughput / rejected.</summary>
    public ObservableCollection<RunValueRow> Metrics { get; } = [];

    /// <summary>Written / rejected / tables / throughput.</summary>
    public ObservableCollection<RunValueRow> SummaryStats { get; } = [];

    /// <summary>Per-table progress.</summary>
    public ObservableCollection<RunTableProgressRow> Tables { get; } = [];

    /// <summary>Last four activity lines. Full log is capped at 200.</summary>
    public ObservableCollection<RunActivityRow> RecentActivity { get; } = [];

    /// <summary>Visible rejection groups (after table / retryable filters).</summary>
    public ObservableCollection<RejectionGroup> RejectionGroups { get; } = [];

    /// <summary>ComboBox items for the rejection table filter.</summary>
    public ObservableCollection<string> TableFilters { get; } = [AllTablesFilter];

    /// <summary>Only <see cref="IWpfGenerationService.GenerateAsync"/> caller. First run and retry both go through here.</summary>
    public async Task<GenerationResult> ExecuteAsync(
        GenerationConfig config,
        string environmentHost,
        IReadOnlyList<string> tables,
        int plannedTotal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tables);
        if (_generation is null)
            throw new InvalidOperationException("Generation service is not configured.");

        _lastConfig = config with { AllowRiskyBogusValues = false };
        _environmentHost = environmentHost;
        _plannedTables = tables;
        _plannedTotal = plannedTotal;
        _seed = config.Seed;
        CurrentRunId = Guid.NewGuid();

        if (!config.AllowRiskyBogusValues && ContainsRiskyBogus(config))
        {
            if (!await ConfirmRiskyBogusAsync())
                throw new OperationCanceledException();
            config = config with { AllowRiskyBogusValues = true };
        }

        await LoadKeepWindowOpenAsync();
        StartRun(environmentHost, config.Seed, plannedTotal, tables);

        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new Progress<ProgressUpdate>(u => AcceptProgress(u, tables, plannedTotal));

        try
        {
            var result = await _generation.GenerateAsync(config, progress, _runCts.Token);
            ApplyResult(result, config.Seed, environmentHost, config);
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusHeadline = "Cancelled";
            throw;
        }
        finally
        {
            IsRunning = false;
            // Hold the completed sheet up until manual Close when the user asked to keep it open.
            IsSheetVisible = KeepWindowOpen;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    /// <summary>Resets the sheet for a new run.</summary>
    public void StartRun(string environmentHost, int seed, int plannedTotal, IReadOnlyList<string> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        _environmentHost = environmentHost;
        _seed = seed;
        _plannedTotal = plannedTotal;
        _plannedTables = tables;
        _tableWritten.Clear();

        IsRunning = true;
        IsSheetVisible = true;
        IsIndeterminate = true;
        StatusHeadline = "Generating…";
        OverallPercent = 0;
        OverallPercentLabel = "0";
        RowsWrittenLabel = 0.ToString("N0");
        RunDescription = BuildRunDescription(0, plannedTotal, environmentHost, seed);

        Tables.Clear();
        foreach (var name in tables)
            Tables.Add(new RunTableProgressRow(name, 0, "0 / 0", "Queued", "Pending", "Normal"));

        Metrics.Clear();
        Metrics.Add(new RunValueRow("Elapsed", "0s", "Normal"));
        Metrics.Add(new RunValueRow("Remaining", "—", "Muted"));
        Metrics.Add(new RunValueRow("Throughput", "0/min", "Normal"));
        Metrics.Add(new RunValueRow("Rejected", "0", "Normal"));
    }

    /// <summary>Projects a pipeline snapshot onto the sheet.</summary>
    public void AcceptProgress(ProgressUpdate u, IReadOnlyList<string> plannedTables, int plannedTotal)
    {
        ArgumentNullException.ThrowIfNull(u);
        ArgumentNullException.ThrowIfNull(plannedTables);

        if (!string.IsNullOrEmpty(u.EntityName))
        {
            _tableWritten[u.EntityName] = u.RecordsCreated;
            UpdateTableRow(u);
        }

        if (u.BatchesCompleted > 0)
            IsIndeterminate = false;

        var written = _tableWritten.Values.Sum();
        var pct = plannedTotal > 0 ? Math.Clamp(100.0 * written / plannedTotal, 0, 100) : 0;
        OverallPercent = pct;
        OverallPercentLabel = ((int)Math.Round(pct)).ToString(CultureInfo.InvariantCulture);
        RowsWrittenLabel = written.ToString("N0");
        RunDescription = BuildRunDescription(written, plannedTotal, _environmentHost, _seed);

        if (!string.Equals(StatusHeadline, "Cancelling…", StringComparison.Ordinal))
            StatusHeadline = "Generating…";

        RefreshMetrics(u, written, plannedTotal);
        var entity = string.IsNullOrEmpty(u.EntityName) ? "" : $"  {u.EntityName}";
        AppendActivity($"{u.Phase}{entity}  {u.RecordsCreated:N0}/{u.TotalRecords:N0}");
    }

    /// <summary>Projects a finished <see cref="GenerationResult"/> onto the summary. Keeps <paramref name="config"/> for retry.</summary>
    public void ApplyResult(GenerationResult result, int seed, string environmentHost, GenerationConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (config is not null)
            _lastConfig = config with { AllowRiskyBogusValues = false };
        _environmentHost = environmentHost;
        _seed = seed;
        if (CurrentRunId == Guid.Empty)
            CurrentRunId = Guid.NewGuid();

        IsRunning = false;
        IsIndeterminate = false;

        var written = result.TotalRecords;
        var rejected = result.Errors.Count;
        var planned = _plannedTotal > 0 ? _plannedTotal : written;
        var pct = planned > 0 ? Math.Clamp(100.0 * written / planned, 0, 100) : 100;
        OverallPercent = pct;
        OverallPercentLabel = ((int)Math.Round(pct)).ToString(CultureInfo.InvariantCulture);
        RowsWrittenLabel = written.ToString("N0");
        RunDescription = BuildRunDescription(written, planned, environmentHost, seed);

        RunMetaLine = $"Finished {DateTime.Now:d MMM yyyy, HH:mm} · {FormatDuration(result.Elapsed)} · seed {seed}";
        ApplyOutcome(written, rejected, result.Elapsed, tableCount: CountTables(result, _plannedTables));
        StatusHeadline = OutcomeHeadline;

        ReplaceGroups(result.Errors
            .GroupBy(e => (e.EntityLogicalName, e.ErrorMessage))
            .Select(BuildGroup));

        AppendActivity($"Finished — {written:N0} written, {rejected:N0} rejected");
    }

    /// <summary>History hydration: stats only, no rejection rows, retry disabled. Does not change <see cref="CurrentRunId"/>.</summary>
    public void HydrateFrom(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);

        _lastConfig = null;
        IsRunning = false;
        IsIndeterminate = false;
        _environmentHost = "";
        _seed = 0;
        _plannedTotal = run.TotalRecords;
        _plannedTables = run.EntityNames;

        RunMetaLine = $"Finished {run.Timestamp.LocalDateTime:d MMM yyyy, HH:mm} · {FormatDuration(run.Duration)} · historical";
        ApplyOutcome(run.TotalRecords, run.ErrorCount, run.Duration, run.EntityNames.Length);
        ReplaceGroups([]);
        RefreshRetryChrome();

        OverallPercent = run.Succeeded ? 100 : OverallPercent;
        RowsWrittenLabel = run.TotalRecords.ToString("N0");
        RunDescription = $"{run.TotalRecords:N0} rows · {run.EntityNames.Length} tables";
    }

    private bool CanRetrySelected() =>
        _generation is not null
        && _lastConfig is not null
        && _allRejectionGroups.Any(g => g.IsRetryable && g.IsSelectedForRetry && g.RowCount > 0);

    /// <summary>Rebuilds config from the stored snapshot for selected retryable tables. No events.</summary>
    [RelayCommand(CanExecute = nameof(CanRetrySelected))]
    private async Task RetrySelectedAsync()
    {
        if (_lastConfig is null || _generation is null)
            return;

        var selected = _allRejectionGroups
            .Where(g => g.IsRetryable && g.IsSelectedForRetry && g.RowCount > 0)
            .ToList();
        if (selected.Count == 0)
            return;

        var names = selected
            .Select(g => g.TableName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in selected)
            counts[group.TableName] = counts.GetValueOrDefault(group.TableName) + group.RowCount;

        Dictionary<string, Dictionary<string, DataGen.Core.Rules.FieldRule>>? rules = null;
        if (_lastConfig.FieldRules is { } existing)
        {
            rules = existing
                .Where(kv => names.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            if (rules.Count == 0)
                rules = null;
        }

        var retryConfig = _lastConfig with
        {
            EntityLogicalNames = names,
            RecordCounts = counts,
            FieldRules = rules,
        };

        await ExecuteAsync(retryConfig, _environmentHost, names, counts.Values.Sum());
    }

    private async Task<bool> ConfirmRiskyBogusAsync()
    {
        if (ConfirmRiskyBogus is not null)
            return await ConfirmRiskyBogus();
        if (_dialogs is null)
            return false;

        var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = "Allow risky generated values",
            Content = "This run includes Bogus endpoints that can produce routable, financial, or external values. Continue?",
            PrimaryButtonText = "Allow",
            CloseButtonText = "Cancel",
        });
        return result == ContentDialogResult.Primary;
    }

    private static bool ContainsRiskyBogus(GenerationConfig config)
    {
        if (config.FieldRules is null)
            return false;

        foreach (var columns in config.FieldRules.Values)
        {
            foreach (var rule in columns.Values)
            {
                if (rule is BogusRule bogus && BogusCatalogQuery.IsRisky(bogus.Api, bogus.Endpoint))
                    return true;
            }
        }

        return false;
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (!IsRunning)
            return;

        var confirmed = _dialogs is null;
        if (_dialogs is not null)
        {
            try
            {
                var result = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
                {
                    Title = "Cancel run",
                    Content = "Cancel stops further writes. Rows already written are not rolled back.",
                    PrimaryButtonText = "Cancel run",
                    CloseButtonText = "Keep running",
                });
                confirmed = result == ContentDialogResult.Primary;
            }
            catch (Exception)
            {
                return;
            }
        }

        if (!confirmed)
            return;

        StatusHeadline = "Cancelling…";
        _runCts?.Cancel();
    }

    [RelayCommand]
    private void ExportRejectedCsv()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Directory.CreateDirectory(downloads);
        var path = Path.Combine(downloads, $"seedbomb-rejected-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Table,Cause,Rows,Disposition,Retryable");
        foreach (var group in _allRejectionGroups)
        {
            sb.Append(Csv(group.TableName)).Append(',');
            sb.Append(Csv(group.CauseText)).Append(',');
            sb.Append(group.RowCount).Append(',');
            sb.Append(Csv(group.DispositionLabel)).Append(',');
            sb.AppendLine(group.IsRetryable ? "true" : "false");
        }

        File.WriteAllText(path, sb.ToString());
    }

    [RelayCommand]
    private void OpenInHistory() => _navigator?.Navigate(typeof(HistoryPage));

    /// <summary>Manually dismisses the completed overlay (shown once the run finishes).</summary>
    [RelayCommand]
    private void CloseSheet() => IsSheetVisible = false;

    [RelayCommand]
    private async Task ShowFullLogAsync()
    {
        if (_dialogs is null)
            return;

        var body = _activityLog.Count == 0
            ? "No activity."
            : string.Join(Environment.NewLine, _activityLog.Select(l => l.Line));

        try
        {
            await _dialogs.ShowAlertAsync("Activity log", body, "Close");
        }
        catch (Exception)
        {
            // Dialog host missing.
        }
    }

    partial void OnKeepWindowOpenChanged(bool value)
    {
        if (_suppressKeepPersist || _settings is null)
            return;
        _ = PersistKeepWindowOpenAsync(value);
    }

    partial void OnSelectedTableFilterChanged(string value) => RebuildVisibleGroups();

    partial void OnRetryableOnlyChanged(bool value) => RebuildVisibleGroups();

    private async Task LoadKeepWindowOpenAsync()
    {
        if (_settings is null)
            return;

        try
        {
            var loaded = await _settings.LoadAsync();
            _suppressKeepPersist = true;
            KeepWindowOpen = loaded.KeepRunSheetOpen;
        }
        catch (Exception)
        {
            // Keep default.
        }
        finally
        {
            _suppressKeepPersist = false;
        }
    }

    private async Task PersistKeepWindowOpenAsync(bool value)
    {
        if (_settings is null)
            return;

        try
        {
            var loaded = await _settings.LoadAsync();
            await _settings.SaveAsync(loaded with { KeepRunSheetOpen = value });
        }
        catch (Exception)
        {
            // Settings I/O is best-effort.
        }
    }

    private void ApplyOutcome(int written, int rejected, TimeSpan elapsed, int tableCount)
    {
        var hasRejects = rejected > 0;
        OutcomeBannerStyle = hasRejects ? "DG.WarningBanner" : "DG.InfoBanner";
        OutcomeGlyph = hasRejects ? SymbolRegular.Warning24 : SymbolRegular.CheckmarkCircle24;
        OutcomeHeadline = hasRejects
            ? $"Completed with {rejected:N0} rejected rows"
            : "Completed";
        OutcomeDetail = $"Wrote {written:N0} rows. Nothing was rolled back.";

        var rpm = elapsed.TotalMinutes > 0 ? written / elapsed.TotalMinutes : 0;
        SummaryStats.Clear();
        SummaryStats.Add(new RunValueRow("Written", written.ToString("N0"), "Normal"));
        SummaryStats.Add(new RunValueRow("Rejected", rejected.ToString("N0"), hasRejects ? "Warning" : "Normal"));
        SummaryStats.Add(new RunValueRow("Tables", tableCount.ToString(CultureInfo.InvariantCulture), "Normal"));
        SummaryStats.Add(new RunValueRow("Throughput", $"{rpm:N0}/min", "Normal"));
    }

    private static RejectionGroup BuildGroup(IGrouping<(string EntityLogicalName, string ErrorMessage), BatchError> grouping)
    {
        var retryable = grouping.Any(RejectionClassifier.IsRetryable);
        var message = grouping.Key.ErrorMessage;
        var hint = retryable
            ? "Transient throttle or timeout — safe to retry."
            : message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                ? "Duplicate key — fix the source data or rule before retry."
                : "Needs a data or plugin fix before retry.";

        return new RejectionGroup
        {
            CauseText = message,
            CauseHint = hint,
            TableName = grouping.Key.EntityLogicalName,
            RowCount = grouping.Count(),
            IsRetryable = retryable,
            DispositionLabel = retryable ? "Retryable" : "Needs a fix",
            DispositionKey = retryable ? "Retryable" : "FixFirst",
            IsSelectedForRetry = retryable,
        };
    }

    private void ReplaceGroups(IEnumerable<RejectionGroup> groups)
    {
        foreach (var group in _allRejectionGroups)
            group.PropertyChanged -= OnRejectionGroupPropertyChanged;

        _allRejectionGroups.Clear();
        foreach (var group in groups)
        {
            _allRejectionGroups.Add(group);
            group.PropertyChanged += OnRejectionGroupPropertyChanged;
        }

        TableFilters.Clear();
        TableFilters.Add(AllTablesFilter);
        foreach (var name in _allRejectionGroups
                     .Select(g => g.TableName)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            TableFilters.Add(name);
        }

        SelectedTableFilter = AllTablesFilter;
        RetryableOnly = false;
        RebuildVisibleGroups();
        RefreshRetryChrome();
    }

    private void RebuildVisibleGroups()
    {
        RejectionGroups.Clear();
        foreach (var group in _allRejectionGroups)
        {
            if (RetryableOnly && !group.IsRetryable)
                continue;
            if (!string.Equals(SelectedTableFilter, AllTablesFilter, StringComparison.Ordinal)
                && !string.Equals(group.TableName, SelectedTableFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            RejectionGroups.Add(group);
        }
    }

    private void OnRejectionGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RejectionGroup.IsSelectedForRetry) or null)
            RefreshRetryChrome();
    }

    private void RefreshRetryChrome()
    {
        var selectedRows = _allRejectionGroups
            .Where(g => g.IsRetryable && g.IsSelectedForRetry)
            .Sum(g => g.RowCount);
        var rows = _allRejectionGroups.Sum(g => g.RowCount);
        RetryButtonLabel = $"Retry {selectedRows} selected";
        RejectionFooterLabel = $"{_allRejectionGroups.Count} causes · {rows} rows · {selectedRows} selected for retry";
        RetrySelectedCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTableRow(ProgressUpdate u)
    {
        var pct = u.TotalRecords > 0 ? 100.0 * u.RecordsCreated / u.TotalRecords : 0;
        var done = u.TotalRecords > 0 && u.RecordsCreated >= u.TotalRecords;
        var row = new RunTableProgressRow(
            u.EntityName,
            pct,
            $"{u.RecordsCreated:N0} / {u.TotalRecords:N0}",
            done ? "Done" : "Writing",
            done ? "Done" : "Active",
            done ? "Normal" : "SemiBold");

        for (var i = 0; i < Tables.Count; i++)
        {
            if (string.Equals(Tables[i].TableName, u.EntityName, StringComparison.OrdinalIgnoreCase))
            {
                Tables[i] = row;
                return;
            }
        }

        Tables.Add(row);
    }

    private void RefreshMetrics(ProgressUpdate u, int written, int plannedTotal)
    {
        var remaining = "—";
        var remainingKind = "Muted";
        if (u.RecordsPerMinute > 0 && plannedTotal > written)
        {
            remaining = FormatDuration(TimeSpan.FromMinutes((plannedTotal - written) / u.RecordsPerMinute));
            remainingKind = "Normal";
        }

        Metrics.Clear();
        Metrics.Add(new RunValueRow("Elapsed", FormatDuration(u.Elapsed), "Normal"));
        Metrics.Add(new RunValueRow("Remaining", remaining, remainingKind));
        Metrics.Add(new RunValueRow("Throughput", $"{u.RecordsPerMinute:N0}/min", "Normal"));
        Metrics.Add(new RunValueRow("Rejected", "0", "Normal"));
    }

    private void AppendActivity(string line)
    {
        _activityLog.Add(new RunActivityRow(line, "Normal"));
        if (_activityLog.Count > ActivityLogCap)
            _activityLog.RemoveAt(0);

        RecentActivity.Clear();
        foreach (var row in _activityLog.TakeLast(RecentActivityCap))
            RecentActivity.Add(row);
    }

    private string BuildRunDescription(int written, int planned, string host, int seed)
    {
        var rules = _lastConfig?.FieldRules?.Sum(t => t.Value.Count) ?? 0;
        var dest = string.IsNullOrWhiteSpace(host) ? "Dataverse" : host;
        return $"{written:N0} of {planned:N0} rows written to {dest} · {rules} rules · seed {seed}";
    }

    private static int CountTables(GenerationResult result, IReadOnlyList<string> planned)
    {
        if (planned.Count > 0)
            return planned.Count;
        return result.CreatedRecords.Count;
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{Math.Max(0, (int)t.TotalSeconds)}s";
    }

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return value;
    }
}

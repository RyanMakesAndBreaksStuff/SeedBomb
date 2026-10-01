using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SeedBomb.Services.Export;
using SeedBomb.Services.History;
using SeedBomb.Services.Navigation;
using SeedBomb.Views.Pages;
using System.Collections.ObjectModel;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace SeedBomb.ViewModels;

/// <summary>One calendar day of run history.</summary>
/// <param name="DayLabel">Today, Yesterday, or a formatted date.</param>
/// <param name="Runs">Runs on that local date, newest first.</param>
public sealed record HistoryDayGroup(string DayLabel, IReadOnlyList<RunRecord> Runs);

/// <summary>Day header row in the flattened History list.</summary>
/// <param name="DayLabel">Today, Yesterday, or a formatted date.</param>
public sealed record HistoryDayHeader(string DayLabel);

/// <summary>ViewModel for the History page.</summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly IRunHistoryService _historyService;
    private readonly ILogger<HistoryViewModel> _logger;
    private readonly RunViewModel? _run;
    private readonly IAppNavigator? _navigator;
    private readonly ISnackbarService? _snackbar;
    private readonly IContentDialogService? _dialogs;
    private CancellationTokenSource? _navCts;

    /// <summary>Test seam: overrides the export destination folder. Null uses the real Downloads folder.</summary>
    internal string? ExportDirectoryOverride { get; set; }

    /// <summary>Test seam: answers the Clear all confirmation without the content dialog.</summary>
    internal Func<Task<bool>>? ConfirmClear { get; set; }

    /// <summary>Initialises the view-model.</summary>
    /// <param name="historyService">Run history service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="run">Optional live run. Tests keep the 2-arg ctor.</param>
    /// <param name="navigator">Optional navigator to <see cref="RunSummaryPage"/>.</param>
    /// <param name="snackbar">Optional snackbar for I/O failures. Appended last so existing 2-arg tests compile.</param>
    /// <param name="dialogs">Optional dialog host for the Clear all confirmation. Without it nothing is cleared.</param>
    public HistoryViewModel(
        IRunHistoryService historyService,
        ILogger<HistoryViewModel> logger,
        RunViewModel? run = null,
        IAppNavigator? navigator = null,
        ISnackbarService? snackbar = null,
        IContentDialogService? dialogs = null)
    {
        _historyService = historyService;
        _logger = logger;
        _run = run;
        _navigator = navigator;
        _snackbar = snackbar;
        _dialogs = dialogs;
    }

    /// <summary>All loaded run records.</summary>
    public ObservableCollection<RunRecord> Runs { get; } = [];

    /// <summary>Flattened day headers and run rows for the virtualized History list.</summary>
    public ObservableCollection<object> FlatItems { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>Gets a value indicating whether the history list is empty.</summary>
    public bool IsEmpty => !Runs.Any();

    /// <summary>Gets the header summary for the loaded run history.</summary>
    public string HistorySummary =>
        Runs.Count == 0
            ? "No runs yet"
            : $"{Runs.Count:N0} runs · {Runs.Sum(r => r.TotalRecords):N0} total records created";

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync()
    {
        _navCts?.Cancel();
        _navCts?.Dispose();
        _navCts = new CancellationTokenSource();
        try
        {
            await LoadAsync(_navCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public override Task OnNavigatedFromAsync()
    {
        _navCts?.Cancel();
        return Task.CompletedTask;
    }

    partial void OnSearchTextChanged(string value) => RebuildDayGroups();

    [RelayCommand]
    private async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var runs = await _historyService.GetRunsAsync(ct);
            ct.ThrowIfCancellationRequested();
            Runs.Clear();
            foreach (var run in runs)
                Runs.Add(run);
            RebuildDayGroups();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HistorySummary));

            if (_historyService.LoadWarning is { } warning)
                _snackbar?.Show("Run history", warning, ControlAppearance.Caution, null, TimeSpan.FromSeconds(6));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load run history");
            _snackbar?.Show("Couldn't load run history", ex.Message,
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        // WR-010: History is the only record of what was written where — confirm first, like
        // every other destructive action.
        if (!await ConfirmClearAsync())
            return;

        try
        {
            await _historyService.ClearAsync();
            Runs.Clear();
            RebuildDayGroups();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HistorySummary));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear history");
            _snackbar?.Show("Couldn't clear history", ex.Message,
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    private async Task<bool> ConfirmClearAsync()
    {
        if (ConfirmClear is not null)
            return await ConfirmClear();
        return _dialogs is not null && await _dialogs.ConfirmAsync(
            "Clear history",
            "Delete every run from History? It is the only record of what was written to which environment. This cannot be undone.",
            "Clear all");
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        try
        {
            var directory = ExportDirectoryOverride ?? ExportPaths.Downloads();
            var path = System.IO.Path.Combine(
                directory,
                $"seedbomb-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var lines = new List<string>
            {
                "Timestamp,Entities,TotalRecords,Duration,Status,Errors,Environment,User,Profile"
            };

            foreach (var run in Runs)
            {
                var entities = string.Join("|", run.EntityNames);
                var duration = $"{(int)run.Duration.TotalMinutes:00}:{run.Duration.Seconds:00}";
                var status = run.Succeeded ? "Success" : "Failed";
                lines.Add(string.Join(",",
                    CsvField.Escape(run.Timestamp.ToString("O")),
                    CsvField.Escape(entities),
                    CsvField.Escape(run.TotalRecords.ToString()),
                    CsvField.Escape(duration),
                    CsvField.Escape(status),
                    CsvField.Escape(run.ErrorCount.ToString()),
                    CsvField.Escape(run.Environment),
                    CsvField.Escape(run.User),
                    CsvField.Escape(run.Profile)));
            }

            await System.IO.File.WriteAllLinesAsync(path, lines);
            _logger.LogInformation("Exported history to {Path}", path);
            _snackbar?.Show("History exported", path,
                ControlAppearance.Success, null, TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export history CSV");
            _snackbar?.Show("Export failed", ex.Message,
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    [RelayCommand]
    private void OpenRun(RunRecord? run)
    {
        if (run is null)
            return;

        if (_run is not null && run.Id == _run.CurrentRunId)
            _run.ShowLive();
        else
            _run?.ShowHistorical(run);
        _navigator?.Navigate(typeof(RunSummaryPage));
    }

    private void RebuildDayGroups()
    {
        IEnumerable<RunRecord> source = Runs;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            source = Runs.Where(r =>
                r.EntityNames.Any(n => n.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                || r.Environment.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || r.User.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || r.Profile.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        var groups = source
            .GroupBy(r => r.Timestamp.LocalDateTime.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryDayGroup(
                FormatDayLabel(g.Key),
                g.OrderByDescending(r => r.Timestamp).ToList()));

        FlatItems.Clear();
        foreach (var group in groups)
        {
            FlatItems.Add(new HistoryDayHeader(group.DayLabel));
            foreach (var run in group.Runs)
                FlatItems.Add(run);
        }
    }

    private static string FormatDayLabel(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)
            return "Today";
        if (date == today.AddDays(-1))
            return "Yesterday";
        return date.ToString("ddd d MMM yyyy");
    }
}
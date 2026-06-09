using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Desktop.Services.History;
using Microsoft.Extensions.Logging;

namespace DataGen.Desktop.ViewModels;

/// <summary>ViewModel for the History page.</summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly IRunHistoryService _historyService;
    private readonly ILogger<HistoryViewModel> _logger;

    /// <summary>Initialises the view-model.</summary>
    /// <param name="historyService">Run history service.</param>
    /// <param name="logger">Logger.</param>
    public HistoryViewModel(IRunHistoryService historyService, ILogger<HistoryViewModel> logger)
    {
        _historyService = historyService;
        _logger = logger;

        var cvs = new CollectionViewSource { Source = Runs };
        cvs.Filter += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                e.Accepted = true;
                return;
            }

            if (e.Item is RunRecord run)
                e.Accepted = run.EntityNames.Any(n =>
                    n.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            else
                e.Accepted = false;
        };

        RunsView = cvs.View;
    }

    /// <summary>Gets the filtered view over <see cref="Runs"/>.</summary>
    public ICollectionView RunsView { get; }

    /// <summary>All loaded run records.</summary>
    public ObservableCollection<RunRecord> Runs { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => RunsView.Refresh();

    /// <inheritdoc />
    public override Task OnNavigatedToAsync()
    {
        LoadCommand.Execute(null);
        return Task.CompletedTask;
    }

    /// <summary>Gets a value indicating whether the history list is empty.</summary>
    public bool IsEmpty => !Runs.Any();

    /// <summary>Gets the header summary for the loaded run history.</summary>
    public string HistorySummary =>
        Runs.Count == 0
            ? "No runs yet"
            : $"{Runs.Count:N0} runs · {Runs.Sum(r => r.TotalRecords):N0} total records created";

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var runs = await _historyService.GetRunsAsync();
            Runs.Clear();
            foreach (var run in runs)
                Runs.Add(run);
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HistorySummary));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load run history");
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _historyService.ClearAsync();
            Runs.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HistorySummary));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear history");
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                $"datagen-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var lines = new List<string>
            {
                "Timestamp,Entities,TotalRecords,Duration,Status,Errors"
            };

            foreach (var run in Runs)
            {
                var entities = string.Join("|", run.EntityNames);
                var duration = $"{(int)run.Duration.TotalMinutes:00}:{run.Duration.Seconds:00}";
                var status = run.Succeeded ? "Success" : "Failed";
                lines.Add(string.Join(",",
                    EscapeCsv(run.Timestamp.ToString("O")),
                    EscapeCsv(entities),
                    EscapeCsv(run.TotalRecords.ToString()),
                    EscapeCsv(duration),
                    EscapeCsv(status),
                    EscapeCsv(run.ErrorCount.ToString())));
            }

            await System.IO.File.WriteAllLinesAsync(path, lines);
            _logger.LogInformation("Exported history to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export history CSV");
        }
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('\"') && !value.Contains('\r') && !value.Contains('\n'))
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

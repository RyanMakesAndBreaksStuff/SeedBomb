using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Exceptions;
using DataGen.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.Services.Dataverse;
using System.ServiceModel;

namespace Seedbomb.ViewModels.Controls;

/// <summary>Owns temporary picker selection and ignores superseded reads.</summary>
public sealed partial class LookupRecordPickerViewModel : ObservableObject, IDisposable
{
    private readonly ILookupRecordSource _source;
    private readonly ILogger<LookupRecordPickerViewModel> _logger;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _load;
    private LookupAttributeMetadata? _attribute;
    private int _generation;
    private int _page;
    private string? _cookie;
    private bool _single;
    private bool _disposed;

    /// <summary>Creates an empty picker; Initialize supplies an editor session.</summary>
    public LookupRecordPickerViewModel(ILookupRecordSource source,
        ILogger<LookupRecordPickerViewModel> logger)
    {
        _source = source;
        _logger = logger;
        Selected.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanAccept));
    }

    /// <summary>Current page; selection is stored separately.</summary>
    public ObservableCollection<LookupRecord> Results { get; } = [];
    /// <summary>Detached selected identities in authored order.</summary>
    public ObservableCollection<LookupRuleValue> Selected { get; } = [];
    /// <summary>Metadata-derived allowed targets.</summary>
    public ObservableCollection<string> Targets { get; } = [];

    [ObservableProperty] private string? _selectedTarget;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private LookupRecord? _selectedResult;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(CanAccept))]
    private string _errorMessage = "";
    [ObservableProperty] private string _status = "Search by name prefix or full GUID.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private bool _canReadNext;

    /// <summary>True when the current operation failed.</summary>
    public bool HasError => ErrorMessage.Length > 0;
    /// <summary>Whether the dialog may return its selected values.</summary>
    public bool CanAccept => !_disposed && !(_lifetime?.IsCancellationRequested ?? false) && !IsBusy && !HasError
        && (_single ? Selected.Count == 1 : Selected.Count >= 2);

    /// <summary>Starts one dialog lifetime using copies of the supplied identities.</summary>
    public void Initialize(LookupAttributeMetadata attribute, IReadOnlyList<LookupRuleValue> initial,
        bool single, CancellationToken ownerToken)
    {
        if (_lifetime is not null) throw new InvalidOperationException("A picker instance is used for one dialog only.");
        _attribute = attribute;
        _single = single;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
        foreach (var target in (attribute.Targets ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            Targets.Add(target);
        foreach (var value in initial) Selected.Add(value with { });
        SelectedTarget = Targets.FirstOrDefault();
    }

    partial void OnSelectedTargetChanged(string? value) => InvalidateResults();
    partial void OnSearchTextChanged(string value) => InvalidateResults();

    private void InvalidateResults()
    {
        _generation++;
        CancelPendingRead();
        Results.Clear();
        SelectedResult = null;
        _page = 0;
        _cookie = null;
        CanReadNext = false;
        IsBusy = false;
        ErrorMessage = "";
        Status = "Search by name prefix or full GUID.";
    }

    private void CancelPendingRead()
    {
        var pending = _load;
        _load = null;
        pending?.Cancel();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SearchAsync(CancellationToken ct) => LoadPageAsync(reset: true, ct);

    private bool CanNextPage() => CanReadNext && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync(CancellationToken ct) => LoadPageAsync(reset: false, ct);

    private async Task LoadPageAsync(bool reset, CancellationToken commandToken)
    {
        if (_disposed || _attribute is null || _lifetime is null || SelectedTarget is null) return;
        CancelPendingRead();
        var generation = ++_generation;
        var own = CancellationTokenSource.CreateLinkedTokenSource(commandToken, _lifetime.Token);
        _load = own;
        var pageNumber = reset ? 1 : _page + 1;
        var request = new LookupSearchRequest(_attribute, SelectedTarget, SearchText,
            pageNumber, reset ? null : _cookie);
        IsBusy = true;
        ErrorMessage = "";
        NextPageCommand.NotifyCanExecuteChanged();
        try
        {
            var page = await _source.ReadAsync(request, own.Token);
            if (_disposed || own.IsCancellationRequested || generation != _generation) return;
            Results.Clear();
            foreach (var record in page.Records) Results.Add(record);
            SelectedResult = null;
            _page = pageNumber;
            _cookie = page.PagingCookie;
            CanReadNext = page.MoreRecords && !string.IsNullOrWhiteSpace(_cookie);
            Status = page.Records.Count == 0 ? "No records match. Change the search or target table."
                : $"Page {_page}: {Results.Count} records" + (page.MoreRecords
                    ? CanReadNext ? " · More matches available" : " · More matches exist; refine the search to continue"
                    : " · End of results");
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || own.IsCancellationRequested || generation != _generation) return;
            _logger.LogError(ex, "Lookup picker read failed for {Target}", request.Target);
            ErrorMessage = DescribeFailure(ex);
            Status = "Search failed. Retry Search or cancel.";
            CanReadNext = false;
        }
        finally
        {
            if (!_disposed && generation == _generation)
            {
                IsBusy = false;
                NextPageCommand.NotifyCanExecuteChanged();
            }
            if (ReferenceEquals(_load, own)) _load = null;
            own.Dispose();
        }
    }

    [RelayCommand]
    private void AddHighlighted()
    {
        if (IsBusy || HasError || SelectedResult is not { } row || !Results.Contains(row)) return;
        if (!Targets.Contains(row.Value.Entity, StringComparer.OrdinalIgnoreCase) || row.Value.Id == Guid.Empty) return;
        if (_single) Selected.Clear();
        if (!Selected.Any(v => v.Id == row.Value.Id && string.Equals(v.Entity, row.Value.Entity, StringComparison.OrdinalIgnoreCase)))
            Selected.Add(row.Value with { });
    }

    [RelayCommand]
    private void Remove(LookupRuleValue? value)
    {
        if (value is not null) Selected.Remove(value);
    }

    private static string DescribeFailure(Exception error) => error switch
    {
        InvalidOperationException invalid => invalid.Message,
        SchemaException => "Lookup metadata could not be loaded. Reconnect and retry Search.",
        FaultException<OrganizationServiceFault> fault =>
            $"Dataverse error {fault.Detail.ErrorCode}: {fault.Detail.Message}",
        DataGenerationException { InnerException: FaultException<OrganizationServiceFault> fault } =>
            $"Dataverse error {fault.Detail.ErrorCode}: {fault.Detail.Message}",
        DataGenerationException => "The lookup read failed after retries. Retry Search or reconnect.",
        _ => "The lookup search could not be completed. Retry Search; details are in the application log.",
    };

    /// <summary>Stops acceptance and cancels outstanding work without awaiting a remote call.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _lifetime?.Cancel();
        CancelPendingRead();
        _lifetime?.Dispose();
        OnPropertyChanged(nameof(CanAccept));
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace Seedbomb.ViewModels;

/// <summary>One cause row on the run summary.</summary>
public sealed partial class RejectionGroup : ObservableObject
{
    /// <summary>Cause headline.</summary>
    public required string CauseText { get; init; }

    /// <summary>What to do about it.</summary>
    public required string CauseHint { get; init; }

    /// <summary>Table logical name.</summary>
    public required string TableName { get; init; }

    /// <summary>Rows in this cause.</summary>
    public int RowCount { get; init; }

    /// <summary>When false the checkbox is disabled and excluded from retry.</summary>
    public bool IsRetryable { get; init; }

    /// <summary>Retryable / Needs a fix.</summary>
    public required string DispositionLabel { get; init; }

    /// <summary><c>Retryable</c> or <c>FixFirst</c>. XAML maps to DG.Success / DG.Warning. No Brush.</summary>
    public required string DispositionKey { get; init; }

    [ObservableProperty] private bool _isSelectedForRetry;
}
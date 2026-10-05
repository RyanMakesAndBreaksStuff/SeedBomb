namespace SeedBomb.Core.Contracts;

/// <summary>
/// Represents an error that occurred while creating or linking records.
/// </summary>
/// <param name="EntityLogicalName">The entity (or link context) that failed.</param>
/// <param name="ErrorMessage">The error message.</param>
/// <param name="FaultCode">The optional Dataverse fault code.</param>
/// <param name="RowCount">
/// Rows this error lost. Per-row faults from an <c>ExecuteMultiple</c> response leave the default
/// of 1; a whole batch that never landed supplies its length, because <c>CreateMultiple</c> is
/// transactional and rejects every row in the request together.
/// </param>
/// <param name="IsTransient">
/// The batch failed on a timeout, network or HTTP 429 fault rather than on its data, so a later
/// resend can succeed unchanged. Set by the writer from the exception; a message carries no type.
/// </param>
public record BatchError(
    string EntityLogicalName,
    string ErrorMessage,
    int? FaultCode,
    int RowCount = 1,
    bool IsTransient = false)
{
    /// <summary>
    /// Generation-order indexes (0-based within the table) of the rows this create error lost; Retry
    /// regenerates exactly these. Empty for link-phase errors, which regenerating rows cannot fix.
    /// </summary>
    public IReadOnlyList<int> RowIndexes { get; init; } = [];
}

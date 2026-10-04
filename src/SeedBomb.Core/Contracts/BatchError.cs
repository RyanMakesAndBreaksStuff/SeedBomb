namespace SeedBomb.Core.Contracts;

/// <summary>
/// Represents an error that occurred during a batch creation operation.
/// </summary>
/// <param name="EntityLogicalName">The entity that failed.</param>
/// <param name="BatchIndex">The index of the batch that failed.</param>
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
    int BatchIndex,
    string ErrorMessage,
    int? FaultCode,
    int RowCount = 1,
    bool IsTransient = false);

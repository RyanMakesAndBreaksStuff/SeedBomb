namespace DataGen.Core.Contracts;

/// <summary>
/// Represents an error that occurred during a batch creation operation.
/// </summary>
/// <param name="EntityLogicalName">The entity that failed.</param>
/// <param name="BatchIndex">The index of the batch that failed.</param>
/// <param name="ErrorMessage">The error message.</param>
/// <param name="FaultCode">The optional Dataverse fault code.</param>
public record BatchError(string EntityLogicalName, int BatchIndex, string ErrorMessage, int? FaultCode);

using SeedBomb.Core.Contracts;

namespace SeedBomb.Services.Generation;

/// <summary>Decides whether a batch error can be retried without a data fix.</summary>
public static class RejectionClassifier
{
    /// <summary>True for service-protection throttling and transient network faults; false for duplicates, plugins, and validation.</summary>
    public static bool IsRetryable(BatchError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        // WR-003: the three Dataverse service-protection codes. 0x80040224 (-2147220956) is
        // IsvUnExpected — a plugin crash that needs a fix, not a retry.
        return error.IsTransient
               || error.FaultCode is -2147015902 or -2147015903 or -2147015898
               || error.ErrorMessage.Contains("429", StringComparison.Ordinal);
    }
}

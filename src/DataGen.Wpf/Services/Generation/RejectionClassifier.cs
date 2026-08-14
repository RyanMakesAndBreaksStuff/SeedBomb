using DataGen.Core.Contracts;

namespace Seedbomb.Services.Generation;

/// <summary>Decides whether a batch error can be retried without a data fix.</summary>
public static class RejectionClassifier
{
    /// <summary>True for throttle/timeout; false for duplicates, plugins, and validation.</summary>
    private const int ThrottleFaultCode = -2147220956;

    /// <summary>True for throttle/timeout; false for duplicates, plugins, and validation.</summary>
    public static bool IsRetryable(BatchError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.FaultCode is ThrottleFaultCode)
            return true;
        var msg = error.ErrorMessage;
        return msg.Contains("throttl", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("429", StringComparison.Ordinal);
    }
}

namespace SeedBomb.Core.Exceptions;

/// <summary>
/// Thrown when a cycle consists entirely of system-required edges and cannot be broken by deferral.
/// </summary>
public class UnbreakableCycleException : CyclicalDependencyException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnbreakableCycleException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="cycles">The unbreakable strongly connected components.</param>
    public UnbreakableCycleException(string message, IReadOnlyList<IReadOnlyList<string>> cycles)
        : base(message, cycles)
    {
    }
}

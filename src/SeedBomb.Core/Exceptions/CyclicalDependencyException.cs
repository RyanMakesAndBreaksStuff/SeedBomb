namespace SeedBomb.Core.Exceptions;

/// <summary>
/// Thrown when cyclical dependencies are detected in the entity graph.
/// </summary>
public class CyclicalDependencyException : SchemaException
{
    /// <summary>
    /// Gets the strongly connected components that form cycles.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CyclicalDependencyException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="cycles">The strongly connected components that form cycles.</param>
    public CyclicalDependencyException(string message, IReadOnlyList<IReadOnlyList<string>> cycles)
        : base(message)
    {
        Cycles = cycles;
    }
}

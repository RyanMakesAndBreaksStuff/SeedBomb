namespace SeedBomb.Core.Exceptions;

/// <summary>
/// Base exception for all data generation errors.
/// </summary>
public class DataGenerationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DataGenerationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public DataGenerationException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DataGenerationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public DataGenerationException(string message, Exception innerException) : base(message, innerException) { }
}

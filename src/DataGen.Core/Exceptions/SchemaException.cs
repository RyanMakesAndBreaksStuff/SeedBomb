namespace DataGen.Core.Exceptions;

/// <summary>
/// Thrown when there is an issue with the Dataverse schema.
/// </summary>
public class SchemaException : DataGenerationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public SchemaException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SchemaException(string message, Exception innerException) : base(message, innerException) { }
}

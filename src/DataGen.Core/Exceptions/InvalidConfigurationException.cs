namespace DataGen.Core.Exceptions;

/// <summary>
/// Thrown when the generation configuration is invalid.
/// </summary>
public class InvalidConfigurationException : DataGenerationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public InvalidConfigurationException(string message) : base(message) { }
}

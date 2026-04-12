namespace DataGen.Core.Exceptions;

/// <summary>
/// Thrown when an unsupported Dataverse attribute type is encountered.
/// </summary>
public class UnsupportedAttributeTypeException : SchemaException
{
    /// <summary>
    /// Gets the attribute type name that is not supported.
    /// </summary>
    public string AttributeTypeName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsupportedAttributeTypeException"/> class.
    /// </summary>
    /// <param name="attributeTypeName">The unsupported attribute type name.</param>
    public UnsupportedAttributeTypeException(string attributeTypeName)
        : base($"Attribute type '{attributeTypeName}' is not supported for data generation.")
    {
        AttributeTypeName = attributeTypeName;
    }
}

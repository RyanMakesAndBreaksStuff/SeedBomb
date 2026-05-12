using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Metadata;

/// <summary>
/// Determines which attributes should be included in data generation
/// based on Dataverse metadata constraints.
/// </summary>
public static class FieldFilter
{
    /// <summary>
    /// Determines whether a field should have a value generated for it.
    /// </summary>
    /// <param name="attr">The attribute metadata to evaluate.</param>
    /// <returns>True if the field should be included in generation.</returns>
    public static bool ShouldGenerateField(AttributeMetadata attr)
    {
        ArgumentNullException.ThrowIfNull(attr);

        return attr.IsValidForCreate == true
            && !IsSystemRequired(attr)
            && !IsCalculatedOrFormula(attr)
            && !IsAutoNumber(attr)
            && !IsBaseCurrencyField(attr.LogicalName)
            && !IsFileOrImageColumn(attr)
            && !IsStateCode(attr);
    }

    /// <summary>
    /// Determines whether the attribute is system-required (managed by Dataverse, not settable by callers).
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <returns>True if the field is system-required.</returns>
    public static bool IsSystemRequired(AttributeMetadata attr)
        => attr.RequiredLevel?.Value == AttributeRequiredLevel.SystemRequired;

    /// <summary>
    /// Determines whether the attribute is a calculated, rollup, or formula field.
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <returns>True if the field is computed server-side.</returns>
    public static bool IsCalculatedOrFormula(AttributeMetadata attr)
        => attr is { SourceType: > 0 };

    /// <summary>
    /// Determines whether the attribute is an auto-number field.
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <returns>True if the field is auto-numbered.</returns>
    public static bool IsAutoNumber(AttributeMetadata attr)
        => attr is StringAttributeMetadata { AutoNumberFormat: not null };

    /// <summary>
    /// Determines whether the field is a base currency field (auto-calculated from exchange rates).
    /// </summary>
    /// <param name="logicalName">The attribute logical name.</param>
    /// <returns>True if the field is a base currency field.</returns>
    public static bool IsBaseCurrencyField(string? logicalName)
        => logicalName?.EndsWith("_base", StringComparison.Ordinal) == true;

    /// <summary>
    /// Determines whether the attribute is a file or image column requiring chunked upload.
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <returns>True if the field requires a dedicated upload API.</returns>
    public static bool IsFileOrImageColumn(AttributeMetadata attr)
        => attr is ImageAttributeMetadata or FileAttributeMetadata;

    /// <summary>
    /// Determines whether the attribute is the statecode field, which should never be set on create.
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <returns>True if the field is statecode.</returns>
    public static bool IsStateCode(AttributeMetadata attr)
        => string.Equals(attr.LogicalName, "statecode", StringComparison.OrdinalIgnoreCase);
}

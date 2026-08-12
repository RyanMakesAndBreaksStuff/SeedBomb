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

        return attr.IsValidForCreate != false
            && !IsSystemRequired(attr)
            && !IsCalculatedOrFormula(attr)
            && !IsAutoNumber(attr)
            && !IsBaseCurrencyField(attr.LogicalName)
            && !IsFileOrImageColumn(attr)
            && !IsStateCode(attr)
            && !IsBpfField(attr);
    }

    /// <summary>
    /// Determines whether the attribute is system-required (managed by Dataverse, not settable by callers).
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <returns>True if the field is system-required.</returns>
    public static bool IsSystemRequired(AttributeMetadata attr)
        => attr.RequiredLevel?.Value == AttributeRequiredLevel.SystemRequired
        && attr is not StringAttributeMetadata;

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
        => attr is StringAttributeMetadata { AutoNumberFormat.Length: > 0 };

    /// <summary>
    /// Determines whether the field is a base currency field (auto-calculated from exchange rates).
    /// </summary>
    /// <param name="logicalName">The attribute logical name.</param>
    /// <returns>True if the field is a base currency field.</returns>
    public static bool IsBaseCurrencyField(string? logicalName)
        => logicalName?.EndsWith("_base", StringComparison.Ordinal) == true
        || string.Equals(logicalName, "exchangerate", StringComparison.OrdinalIgnoreCase);

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

    // traversedpath is StringAttributeMetadata + SystemRequired, so the StringAttributeMetadata
    // carve-out in IsSystemRequired would otherwise let it through. Dataverse owns its value
    // (comma-separated stage GUIDs written by the BPF engine); any caller-supplied value is rejected.
    private static readonly HashSet<string> _bpfFields = ["traversedpath", "stageid", "processid"];

    /// <summary>
    /// Determines whether the attribute is a Business Process Flow system field managed by Dataverse.
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <returns>True if the field is BPF-managed and must not be set on create.</returns>
    public static bool IsBpfField(AttributeMetadata attr)
        => attr.LogicalName is not null && _bpfFields.Contains(attr.LogicalName);
}

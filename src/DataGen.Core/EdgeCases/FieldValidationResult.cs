namespace DataGen.Core.EdgeCases;

/// <summary>
/// The outcome of validating a field for data generation.
/// </summary>
public enum FieldAction
{
    /// <summary>The field can be generated normally.</summary>
    Generate,

    /// <summary>The field should be skipped entirely.</summary>
    Skip,

    /// <summary>The field requires special handling during generation.</summary>
    SpecialHandling,

    /// <summary>The field cannot be generated due to a metadata issue.</summary>
    Fail
}

/// <summary>
/// Represents the result of validating a single field against edge-case rules.
/// </summary>
/// <param name="FieldLogicalName">The logical name of the validated field.</param>
/// <param name="Action">The action to take for this field.</param>
/// <param name="Reason">The reason for skipping or failing, if applicable.</param>
/// <param name="HandlingCategory">The special handling category, if applicable.</param>
public sealed record FieldValidationResult(
    string FieldLogicalName,
    FieldAction Action,
    string? Reason = null,
    string? HandlingCategory = null);

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
public class FieldValidationResult
{
    /// <summary>
    /// Gets or sets the logical name of the validated field.
    /// </summary>
    public required string FieldLogicalName { get; init; }

    /// <summary>
    /// Gets the action to take for this field.
    /// </summary>
    public FieldAction Action { get; private set; } = FieldAction.Generate;

    /// <summary>
    /// Gets the reason for skipping or special handling, if applicable.
    /// </summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// Gets the special handling category, if applicable.
    /// </summary>
    public string? HandlingCategory { get; private set; }

    /// <summary>
    /// Marks the field as OK for normal generation.
    /// </summary>
    public void OK()
    {
        Action = FieldAction.Generate;
        Reason = null;
        HandlingCategory = null;
    }

    /// <summary>
    /// Marks the field to be skipped with the given reason.
    /// </summary>
    /// <param name="reason">Why the field is being skipped.</param>
    public void Skip(string reason)
    {
        Action = FieldAction.Skip;
        Reason = reason;
    }

    /// <summary>
    /// Marks the field as requiring special handling.
    /// </summary>
    /// <param name="category">The handling category identifier.</param>
    public void RequireSpecialHandling(string category)
    {
        Action = FieldAction.SpecialHandling;
        HandlingCategory = category;
    }

    /// <summary>
    /// Marks the field as failed validation.
    /// </summary>
    /// <param name="reason">Why the field cannot be generated.</param>
    public void Fail(string reason)
    {
        Action = FieldAction.Fail;
        Reason = reason;
    }
}

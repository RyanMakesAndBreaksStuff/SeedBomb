using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.EdgeCases;

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

/// <summary>The reason a field needs special handling during generation.</summary>
public enum SpecialHandlingCategory
{
    /// <summary>Multi-select option set.</summary>
    MultiSelect,

    /// <summary>Money field; transactioncurrencyid is silently required.</summary>
    CurrencyValidation,

    /// <summary>DateTime field; see <see cref="FieldValidationResult.DateTimeBehavior"/> for the specific behavior.</summary>
    DateTime,

    /// <summary>Field participates in an alternate key.</summary>
    AlternateKeyUniqueness,

    /// <summary>Rich-text memo field.</summary>
    RichText,

    /// <summary>The polymorphic <c>ownerid</c> lookup.</summary>
    OwnerLookup,

    /// <summary>A polymorphic lookup with more than one target.</summary>
    PolymorphicLookup
}

/// <summary>
/// Represents the result of validating a single field against edge-case rules.
/// </summary>
/// <param name="FieldLogicalName">The logical name of the validated field.</param>
/// <param name="Action">The action to take for this field.</param>
/// <param name="Reason">The reason for skipping or failing, if applicable.</param>
/// <param name="HandlingCategory">The special handling category, if applicable.</param>
/// <param name="DateTimeBehavior">The Dataverse DateTime behavior, set only when <paramref name="HandlingCategory"/> is <see cref="SpecialHandlingCategory.DateTime"/>.</param>
public sealed record FieldValidationResult(
    string FieldLogicalName,
    FieldAction Action,
    string? Reason = null,
    SpecialHandlingCategory? HandlingCategory = null,
    DateTimeBehavior? DateTimeBehavior = null);

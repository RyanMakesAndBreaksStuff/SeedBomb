using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.EdgeCases;

/// <summary>
/// Validates attributes against 14 edge-case categories to determine how each
/// field should be handled during data generation.
/// </summary>
public class EdgeCaseValidator
{
    private readonly ILogger<EdgeCaseValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EdgeCaseValidator"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public EdgeCaseValidator(ILogger<EdgeCaseValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Validates a field against the 14 edge-case categories and returns the appropriate action.
    /// </summary>
    /// <param name="attr">The attribute metadata to validate.</param>
    /// <param name="entity">The owning entity metadata (for alternate key checks).</param>
    /// <returns>A validation result indicating the action to take.</returns>
    public FieldValidationResult Validate(AttributeMetadata attr, EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(attr);
        ArgumentNullException.ThrowIfNull(entity);

        var name = attr.LogicalName ?? "unknown";

        // 1. Calculated/Rollup/Formula — server-computed, skip
        if (attr is { SourceType: > 0 })
        {
            _logger.LogDebug("Skipping {Field}: calculated/rollup/formula", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.Skip, "Calculated/rollup/formula fields are auto-computed");
        }

        // 2. Auto-number — server-generated, skip
        if (attr is StringAttributeMetadata { AutoNumberFormat.Length: > 0 })
        {
            _logger.LogDebug("Skipping {Field}: auto-number", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.Skip, "Auto-number fields are server-generated");
        }

        // 3. Base currency (*_base fields) — auto-calculated from exchange rates, skip
        if (attr.LogicalName?.EndsWith("_base", StringComparison.Ordinal) == true)
        {
            _logger.LogDebug("Skipping {Field}: base currency", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.Skip, "Base currency fields are auto-calculated from exchange rates");
        }

        // 4. File/Image columns — require chunked upload API, skip
        if (attr is ImageAttributeMetadata or FileAttributeMetadata)
        {
            _logger.LogDebug("Skipping {Field}: file/image column", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.Skip, "File/Image fields require dedicated upload API");
        }

        // 5. Status/State — special handling (statecode should never be set on create)
        if (string.Equals(attr.LogicalName, "statecode", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping {Field}: statecode", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.Skip, "statecode defaults to Active on create and should not be set");
        }

        if (string.Equals(attr.LogicalName, "statuscode", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping {Field}: statuscode", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.Skip, "statuscode must match statecode; Dataverse sets default on create");
        }

        // 6. Picklist with no valid options — fail
        if (attr is PicklistAttributeMetadata psMeta)
        {
            if (psMeta.OptionSet?.Options?.Count is null or 0)
            {
                _logger.LogDebug("Failed {Field}: picklist with no options", attr.LogicalName);
                return new FieldValidationResult(name, FieldAction.Fail, "Picklist has no valid options");
            }
            return new FieldValidationResult(name, FieldAction.Generate);
        }

        // 7. Multi-select option sets — special handling
        if (attr is MultiSelectPicklistAttributeMetadata msMeta)
        {
            return msMeta.OptionSet?.Options?.Count is null or 0
                ? new FieldValidationResult(name, FieldAction.Fail, "Multi-select picklist has no valid options")
                : new FieldValidationResult(name, FieldAction.SpecialHandling, HandlingCategory: SpecialHandlingCategory.MultiSelect);
        }

        // 8. Currency fields — transactioncurrencyid is silently required for Money fields
        if (attr is MoneyAttributeMetadata)
        {
            _logger.LogDebug("Special handling for {Field}: currency field", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.SpecialHandling, HandlingCategory: SpecialHandlingCategory.CurrencyValidation);
        }

        // 9. DateTime behavior — affects how values are stored and displayed
        if (attr is DateTimeAttributeMetadata dtMeta)
        {
            var behavior = dtMeta.DateTimeBehavior?.Value ?? "Unknown";
            _logger.LogDebug("Special handling for {Field}: datetime behavior {Behavior}", attr.LogicalName, behavior);
            return new FieldValidationResult(name, FieldAction.SpecialHandling,
                HandlingCategory: SpecialHandlingCategory.DateTime, DateTimeBehavior: dtMeta.DateTimeBehavior);
        }

        // 10. Alternate keys — need uniqueness guarantees
        if (entity.Keys?.Any(k => k.KeyAttributes?.Contains(attr.LogicalName) == true) == true)
        {
            _logger.LogDebug("Special handling for {Field}: part of alternate key", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.SpecialHandling, HandlingCategory: SpecialHandlingCategory.AlternateKeyUniqueness);
        }

        // 11. Rich text fields — require HTML formatting
        if (attr is MemoAttributeMetadata memoMeta &&
            string.Equals(memoMeta.FormatName?.Value, "RichText", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Special handling for {Field}: rich text memo", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.SpecialHandling, HandlingCategory: SpecialHandlingCategory.RichText);
        }

        // 12. UTC offset integers — Format metadata may be null but Dataverse validates against timezonedefinition; skip
        if (attr is IntegerAttributeMetadata &&
            attr.LogicalName?.EndsWith("utcoffset", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogDebug("Skipping {Field}: utcoffset requires timezone definition lookup", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.Skip, "UTC offset fields require valid timezone definition codes; optional address field");
        }

        // 13. Owner-type lookups — ownerid is polymorphic (systemuser + team) but Dataverse
        // auto-defaults it to the calling user, so route it as OwnerLookup before the generic
        // polymorphic check (which would otherwise shadow it).
        if (attr is LookupAttributeMetadata lookupMeta &&
            string.Equals(lookupMeta.LogicalName, "ownerid", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Special handling for {Field}: owner lookup", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.SpecialHandling, HandlingCategory: SpecialHandlingCategory.OwnerLookup);
        }

        // 14. Polymorphic lookups — multiple possible target entities
        if (attr is LookupAttributeMetadata { Targets.Length: > 1 })
        {
            _logger.LogDebug("Special handling for {Field}: polymorphic lookup", attr.LogicalName);
            return new FieldValidationResult(name, FieldAction.SpecialHandling, HandlingCategory: SpecialHandlingCategory.PolymorphicLookup);
        }

        // Default: field is OK for normal generation
        return new FieldValidationResult(name, FieldAction.Generate);
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.EdgeCases;

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

        var result = new FieldValidationResult { FieldLogicalName = attr.LogicalName ?? "unknown" };

        // 1. Calculated/Rollup/Formula — server-computed, skip
        if (attr is { SourceType: > 0 })
        {
            result.Skip("Calculated/rollup/formula fields are auto-computed");
            _logger.LogDebug("Skipping {Field}: calculated/rollup/formula", attr.LogicalName);
            return result;
        }

        // 2. Auto-number — server-generated, skip
        if (attr is StringAttributeMetadata { AutoNumberFormat.Length: > 0 })
        {
            result.Skip("Auto-number fields are server-generated");
            _logger.LogDebug("Skipping {Field}: auto-number", attr.LogicalName);
            return result;
        }

        // 3. Base currency (*_base fields) — auto-calculated from exchange rates, skip
        if (attr.LogicalName?.EndsWith("_base", StringComparison.Ordinal) == true)
        {
            result.Skip("Base currency fields are auto-calculated from exchange rates");
            _logger.LogDebug("Skipping {Field}: base currency", attr.LogicalName);
            return result;
        }

        // 4. File/Image columns — require chunked upload API, skip
        if (attr is ImageAttributeMetadata or FileAttributeMetadata)
        {
            result.Skip("File/Image fields require dedicated upload API");
            _logger.LogDebug("Skipping {Field}: file/image column", attr.LogicalName);
            return result;
        }

        // 5. Status/State — special handling (statecode should never be set on create)
        if (string.Equals(attr.LogicalName, "statecode", StringComparison.OrdinalIgnoreCase))
        {
            result.Skip("statecode defaults to Active on create and should not be set");
            _logger.LogDebug("Skipping {Field}: statecode", attr.LogicalName);
            return result;
        }

        if (string.Equals(attr.LogicalName, "statuscode", StringComparison.OrdinalIgnoreCase))
        {
            result.Skip("statuscode must match statecode; Dataverse sets default on create");
            _logger.LogDebug("Skipping {Field}: statuscode", attr.LogicalName);
            return result;
        }

        // 6. Picklist with no valid options — fail
        if (attr is PicklistAttributeMetadata psMeta)
        {
            if (psMeta.OptionSet?.Options?.Count is null or 0)
            {
                result.Fail("Picklist has no valid options");
                _logger.LogDebug("Failed {Field}: picklist with no options", attr.LogicalName);
            }
            else
            {
                result.OK();
            }
            return result;
        }

        // 7. Multi-select option sets — special handling
        if (attr is MultiSelectPicklistAttributeMetadata msMeta)
        {
            if (msMeta.OptionSet?.Options?.Count is null or 0)
            {
                result.Fail("Multi-select picklist has no valid options");
            }
            else
            {
                result.RequireSpecialHandling("MultiSelect");
            }
            return result;
        }

        // 8. Currency fields — transactioncurrencyid is silently required for Money fields
        if (attr is MoneyAttributeMetadata)
        {
            result.RequireSpecialHandling("CurrencyValidation");
            _logger.LogDebug("Special handling for {Field}: currency field", attr.LogicalName);
            return result;
        }

        // 9. DateTime behavior — affects how values are stored and displayed
        if (attr is DateTimeAttributeMetadata dtMeta)
        {
            var behavior = dtMeta.DateTimeBehavior?.Value ?? "Unknown";
            result.RequireSpecialHandling($"DateTime_{behavior}");
            _logger.LogDebug("Special handling for {Field}: datetime behavior {Behavior}", attr.LogicalName, behavior);
            return result;
        }

        // 10. Alternate keys — need uniqueness guarantees
        if (entity.Keys?.Any(k => k.KeyAttributes?.Contains(attr.LogicalName) == true) == true)
        {
            result.RequireSpecialHandling("AlternateKeyUniqueness");
            _logger.LogDebug("Special handling for {Field}: part of alternate key", attr.LogicalName);
            return result;
        }

        // 11. Rich text fields — require HTML formatting
        if (attr is MemoAttributeMetadata memoMeta &&
            string.Equals(memoMeta.FormatName?.Value, "RichText", StringComparison.OrdinalIgnoreCase))
        {
            result.RequireSpecialHandling("RichText");
            _logger.LogDebug("Special handling for {Field}: rich text memo", attr.LogicalName);
            return result;
        }

        // 12. UTC offset integers — Format metadata may be null but Dataverse validates against timezonedefinition; skip
        if (attr is IntegerAttributeMetadata &&
            attr.LogicalName?.EndsWith("utcoffset", StringComparison.OrdinalIgnoreCase) == true)
        {
            result.Skip("UTC offset fields require valid timezone definition codes; optional address field");
            _logger.LogDebug("Skipping {Field}: utcoffset requires timezone definition lookup", attr.LogicalName);
            return result;
        }

        // 13. Owner-type lookups — ownerid is polymorphic (systemuser + team) but Dataverse
        // auto-defaults it to the calling user, so route it as OwnerLookup before the generic
        // polymorphic check (which would otherwise shadow it).
        if (attr is LookupAttributeMetadata lookupMeta &&
            string.Equals(lookupMeta.LogicalName, "ownerid", StringComparison.OrdinalIgnoreCase))
        {
            result.RequireSpecialHandling("OwnerLookup");
            _logger.LogDebug("Special handling for {Field}: owner lookup", attr.LogicalName);
            return result;
        }

        // 14. Polymorphic lookups — multiple possible target entities
        if (attr is LookupAttributeMetadata { Targets.Length: > 1 })
        {
            result.RequireSpecialHandling("PolymorphicLookup");
            _logger.LogDebug("Special handling for {Field}: polymorphic lookup", attr.LogicalName);
            return result;
        }

        // Default: field is OK for normal generation
        result.OK();
        return result;
    }
}

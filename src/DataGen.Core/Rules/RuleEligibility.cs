using DataGen.Core.Metadata;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Rules;

/// <summary>Reason codes from spec §3.2 — shown verbatim in the Add-rule picker and import reports.</summary>
public enum EligibilityReason
{
    /// <summary>Eligible for a rule.</summary>
    Settable,

    /// <summary>Primary key — assigned by the platform.</summary>
    PlatformKey,

    /// <summary>Auto-numbered by platform.</summary>
    AutoNumber,

    /// <summary>Platform computes this value.</summary>
    Calculated,

    /// <summary>Derived from exchange rate.</summary>
    BaseCurrency,

    /// <summary>Platform-owned state — set Status (reason) instead.</summary>
    StateCode,

    /// <summary>Business process flow bookkeeping — grouped with StateCode in UI copy.</summary>
    BpfBookkeeping,

    /// <summary>File/image — needs the upload API.</summary>
    BinaryUpload,

    /// <summary>Lookup — out of scope in v1.</summary>
    Lookup,

    /// <summary>Type discriminator of a polymorphic lookup (Owner/Customer) — Dataverse never
    /// populates a real OptionSet for it, so a oneOf rule can never be completed.</summary>
    PolymorphicType,

    /// <summary>Not valid for create.</summary>
    NotCreatable,

    /// <summary>MultiSelect — rule editing planned for v2 (D5; listed disabled, not excluded).</summary>
    MultiSelectV2,
}

/// <summary>Classification result for one attribute.</summary>
/// <param name="IsSettable">True = selectable rule target in the picker.</param>
/// <param name="Reason">Why not (or <see cref="EligibilityReason.Settable"/>).</param>
public readonly record struct EligibilityResult(bool IsSettable, EligibilityReason Reason);

/// <summary>
/// Rule-target eligibility (spec S3). Same gates as <see cref="FieldFilter"/>, plus reason codes
/// and two rule-specific gates: lookups and MultiSelect are auto-only (D5, §3.1).
/// Order matters — first matching reason wins, mirroring the §3.2 gate order.
/// </summary>
public static class RuleEligibility
{
    /// <summary>Classifies one attribute as settable-for-rules or excluded-with-reason.</summary>
    public static EligibilityResult Classify(AttributeMetadata attr)
    {
        ArgumentNullException.ThrowIfNull(attr);

        if (attr.IsPrimaryId == true || attr is UniqueIdentifierAttributeMetadata)
            return new(false, EligibilityReason.PlatformKey);
        if (attr is StateAttributeMetadata || FieldFilter.IsStateCode(attr))
            return new(false, EligibilityReason.StateCode);
        if (FieldFilter.IsBpfField(attr))
            return new(false, EligibilityReason.BpfBookkeeping);
        if (FieldFilter.IsAutoNumber(attr))
            return new(false, EligibilityReason.AutoNumber);
        if (FieldFilter.IsCalculatedOrFormula(attr))
            return new(false, EligibilityReason.Calculated);
        if (FieldFilter.IsBaseCurrencyField(attr.LogicalName))
            return new(false, EligibilityReason.BaseCurrency);
        if (FieldFilter.IsFileOrImageColumn(attr))
            return new(false, EligibilityReason.BinaryUpload);
        if (attr is LookupAttributeMetadata)
            return new(false, EligibilityReason.Lookup);
        if (attr is EntityNameAttributeMetadata)
            return new(false, EligibilityReason.PolymorphicType);
        if (attr is MultiSelectPicklistAttributeMetadata)
            return new(false, EligibilityReason.MultiSelectV2);
        if (attr.IsValidForCreate == false)
            return new(false, EligibilityReason.NotCreatable);

        return new(true, EligibilityReason.Settable);
    }
}

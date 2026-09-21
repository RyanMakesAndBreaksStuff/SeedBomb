using SeedBomb.Core.Rules;

namespace SeedBomb.Core.Tests.Rules;

public class RuleEligibilityTests
{
    [Fact]
    public void Statecode_maps_to_STATE_CODE()
    {
        var attr = new StateAttributeMetadata { LogicalName = "statecode" };
        Assert.Equal(EligibilityReason.StateCode, RuleEligibility.Classify(attr).Reason);
    }

    [Fact]
    public void Single_target_lookup_is_settable()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "primarycontactid",
            Targets = ["contact"],
            IsValidForCreate = true,
        };
        var r = RuleEligibility.Classify(attr);
        Assert.True(r.IsSettable);
        Assert.Equal(EligibilityReason.Settable, r.Reason);
    }

    [Fact]
    public void Customer_with_account_and_contact_is_settable()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "customerid",
            Targets = ["account", "contact"],
            IsValidForCreate = true,
        };
        SetAttributeType(attr, AttributeTypeCode.Customer);
        var r = RuleEligibility.Classify(attr);
        Assert.True(r.IsSettable);
        Assert.Equal(EligibilityReason.Settable, r.Reason);
    }

    [Fact]
    public void Owner_type_with_one_target_is_owner_assigned()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "owninguser",
            Targets = ["systemuser"],
            IsValidForCreate = true,
        };
        SetAttributeType(attr, AttributeTypeCode.Owner);
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.OwnerAssigned, r.Reason);
    }

    [Fact]
    public void Ownerid_logical_name_with_one_target_is_owner_assigned()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "ownerid",
            Targets = ["systemuser"],
            IsValidForCreate = true,
        };
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.OwnerAssigned, r.Reason);
    }

    [Fact]
    public void PartyList_maps_to_LOOKUP()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "to",
            Targets = ["activityparty"],
            IsValidForCreate = true,
        };
        SetAttributeType(attr, AttributeTypeCode.PartyList);
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.Lookup, r.Reason);
    }

    [Fact]
    public void Multi_target_lookup_maps_to_LOOKUP()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "regardingobjectid",
            Targets = ["account", "contact"],
            IsValidForCreate = true,
        };
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.Lookup, r.Reason);
    }

    [Fact]
    public void Null_targets_map_to_LOOKUP()
    {
        var attr = new LookupAttributeMetadata { LogicalName = "primarycontactid", Targets = null, IsValidForCreate = true };
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.Lookup, r.Reason);
    }

    [Fact]
    public void Empty_targets_map_to_LOOKUP()
    {
        var attr = new LookupAttributeMetadata { LogicalName = "primarycontactid", Targets = [], IsValidForCreate = true };
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.Lookup, r.Reason);
    }

    [Fact]
    public void Whitespace_target_maps_to_LOOKUP()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "primarycontactid",
            Targets = ["contact", ""],
            IsValidForCreate = true,
        };
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.Lookup, r.Reason);
    }

    [Fact]
    public void Eligible_lookup_shape_that_is_not_creatable_maps_to_NOT_CREATABLE()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "primarycontactid",
            Targets = ["contact"],
            IsValidForCreate = false,
        };
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.NotCreatable, r.Reason);
    }

    [Fact]
    public void Calculated_lookup_retains_calculated_exclusion()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "primarycontactid",
            Targets = ["contact"],
            IsValidForCreate = true,
            SourceType = 1,
        };
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.Calculated, r.Reason);
    }

    [Fact]
    public void Alternate_key_exclusion_is_not_a_core_eligibility_reason()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "primarycontactid",
            Targets = ["contact"],
            IsValidForCreate = true,
        };
        var r = RuleEligibility.Classify(attr);
        Assert.True(r.IsSettable);
        Assert.Equal(EligibilityReason.Settable, r.Reason);
    }

    [Fact]
    public void OwnerAssigned_appends_without_renumbering_existing_reasons()
    {
        Assert.Equal(11, (int)EligibilityReason.MultiSelectV2);
        Assert.Equal(12, (int)EligibilityReason.OwnerAssigned);
    }

    [Fact]
    public void MultiSelect_maps_to_MULTISELECT_V2()
    {
        var attr = new MultiSelectPicklistAttributeMetadata { LogicalName = "preferredcontactmethodcode" };
        Assert.Equal(EligibilityReason.MultiSelectV2, RuleEligibility.Classify(attr).Reason);
    }

    [Fact]
    public void EntityName_maps_to_POLYMORPHIC_TYPE()
    {
        // owneridtype/customeridtype: an EnumAttributeMetadata subtype (Choice in the picker),
        // but Dataverse never populates a real OptionSet for it — a oneOf rule could never be
        // completed if this were left settable.
        var attr = new EntityNameAttributeMetadata { LogicalName = "owneridtype" };
        var r = RuleEligibility.Classify(attr);
        Assert.False(r.IsSettable);
        Assert.Equal(EligibilityReason.PolymorphicType, r.Reason);
    }

    [Fact]
    public void Plain_string_is_settable()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 };
        var r = RuleEligibility.Classify(attr);
        Assert.True(r.IsSettable);
        Assert.Equal(EligibilityReason.Settable, r.Reason);
    }

    private static void SetAttributeType(AttributeMetadata attr, AttributeTypeCode type)
        => typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(attr, type);
}

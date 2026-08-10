using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Tests.Rules;

public class RuleEligibilityTests
{
    [Fact]
    public void Statecode_maps_to_STATE_CODE()
    {
        var attr = new StateAttributeMetadata { LogicalName = "statecode" };
        Assert.Equal(EligibilityReason.StateCode, RuleEligibility.Classify(attr).Reason);
    }

    [Fact]
    public void Lookup_maps_to_LOOKUP()
    {
        var attr = new LookupAttributeMetadata { LogicalName = "primarycontactid" };
        Assert.Equal(EligibilityReason.Lookup, RuleEligibility.Classify(attr).Reason);
    }

    [Fact]
    public void MultiSelect_maps_to_MULTISELECT_V2()
    {
        var attr = new MultiSelectPicklistAttributeMetadata { LogicalName = "preferredcontactmethodcode" };
        Assert.Equal(EligibilityReason.MultiSelectV2, RuleEligibility.Classify(attr).Reason);
    }

    [Fact]
    public void Plain_string_is_settable()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 };
        var r = RuleEligibility.Classify(attr);
        Assert.True(r.IsSettable);
        Assert.Equal(EligibilityReason.Settable, r.Reason);
    }
}

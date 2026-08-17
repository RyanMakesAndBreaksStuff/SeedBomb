using DataGen.Core.Contracts;

namespace DataGen.Core.Tests;

public class EntitySummaryTests
{
    [Theory]
    [InlineData("cr12_site", true, true)]
    [InlineData("new_widget", true, true)]
    [InlineData("contoso_order", true, true)]
    [InlineData("account", false, false)]
    [InlineData("account", true, false)]
    [InlineData("activityfileattachment", true, false)]
    [InlineData("msdyn_flow_actionapprovalmodel", true, false)]
    [InlineData("msdynce_botcontent", true, false)]
    [InlineData("mspp_adplacement", true, false)]
    [InlineData("msfp_survey", true, false)]
    [InlineData("adx_webpage", true, false)]
    [InlineData("spstudio_actionvisual", true, false)]
    [InlineData("powerpagesite_something", true, false)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    public void IsUserCreated_classifies_maker_tables_not_first_party(
        string? logicalName, bool isCustomEntity, bool expected)
    {
        Assert.Equal(expected, EntitySummary.IsUserCreated(logicalName, isCustomEntity));
    }
}

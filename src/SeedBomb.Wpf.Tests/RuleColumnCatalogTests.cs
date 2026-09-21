using SeedBomb.Wpf.Tests.Views;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.ViewModels;
using System.Windows.Data;
using Xunit;

namespace SeedBomb.Wpf.Tests;

[Collection("StaUi")]
public sealed class RuleColumnCatalogTests
{
    [StaFact]
    public void Groups_sort_by_display_name_after_bind_filter_and_reset()
    {
        var catalog = new RuleColumnCatalog();
        var byName = new Dictionary<string, AttributeMetadata>();
        var search = string.Empty;
        var entity = new EntityMetadata { LogicalName = "account" };
        AttributeMetadata[] attributes = [
            Column("disabled_a", "Zulu", false),
            Column("unmapped_a", "Zulu"),
            Column("required_a", "Zulu", required: true),
            Column("mapped_a", "Zulu"),
            Column("disabled_z", "Alpha", false),
            Column("unmapped_z", "Alpha"),
            Column("required_z", "Alpha", required: true),
            Column("mapped_z", "Alpha"),
        ];
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.Attributes))!
            .SetValue(entity, attributes);

        Reset();
        catalog.BindView(c => catalog.Matches(c, search,
            c.StateKey == "Required", c.StateKey == "Mapped", hasProfile: true));
        AssertGroups("Alpha", "Zulu");

        search = "Alpha";
        catalog.ColumnsView!.Refresh();
        AssertGroups("Alpha");
        search = string.Empty;
        catalog.ApplyGrouping();
        catalog.ColumnsView.Refresh();
        AssertGroups("Alpha", "Zulu");

        catalog.SetFilterKey("Disabled");
        catalog.ApplyGrouping();
        Assert.Equal(new[] { "Alpha", "Zulu" },
            catalog.ColumnsView.Cast<PickerColumn>().Select(c => c.DisplayName));
        Assert.All(catalog.ColumnsView.Cast<PickerColumn>(), c => Assert.False(c.IsSelectable));
        catalog.SetFilterKey("All");
        Reset();
        catalog.ApplyGrouping();
        catalog.ColumnsView.Refresh();
        AssertGroups("Alpha", "Zulu");

        void Reset() => catalog.Reset(entity, byName,
            name => name.StartsWith("mapped_", StringComparison.Ordinal),
            c => c.StateKey == "Required");

        void AssertGroups(params string[] expectedNames)
        {
            var groups = catalog.ColumnsView!.Groups!.Cast<CollectionViewGroup>().ToArray();
            Assert.Equal(new[] { "Mapped", "Unmapped · required", "Unmapped", "Disabled" },
                groups.Select(g => (string)g.Name));
            foreach (var group in groups)
                Assert.Equal(expectedNames, group.Items.Cast<PickerColumn>().Select(c => c.DisplayName));
        }
    }

    private static StringAttributeMetadata Column(
        string logicalName, string displayName, bool selectable = true, bool required = false) => new()
    {
        LogicalName = logicalName,
        DisplayName = new Label { UserLocalizedLabel = new LocalizedLabel(displayName, 1033) },
        IsValidForCreate = selectable,
        MaxLength = 100,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(required
            ? AttributeRequiredLevel.ApplicationRequired
            : AttributeRequiredLevel.None),
    };
}

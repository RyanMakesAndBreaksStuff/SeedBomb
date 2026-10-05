using System.Text.Json;
using SeedBomb.Core.Rules;

namespace SeedBomb.Core.Tests.Rules;

public class RequiredLookupPreflightTests
{
    private static LookupAttributeMetadata RequiredLookup(string name, params string[] targets)
    {
        var attr = new LookupAttributeMetadata { LogicalName = name, Targets = targets };
        attr.GetType().GetProperty("RequiredLevel")!.SetValue(
            attr, new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired));
        return attr;
    }

    private static EntityMetadata Meta(string table, params AttributeMetadata[] attrs)
    {
        var meta = new EntityMetadata { LogicalName = table };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, attrs);
        return meta;
    }

    [Fact]
    public void Target_created_earlier_needs_no_rule()
    {
        var meta = Meta("contact", RequiredLookup("new_requiredid", "account"));

        var messages = RequiredLookupPreflight.FindUnsupplied("contact", meta, null, ["Account"]);

        Assert.Empty(messages);
    }

    [Fact]
    public void Target_absent_from_the_run_is_flagged()
    {
        var meta = Meta("contact", RequiredLookup("new_requiredid", "account"));

        var messages = RequiredLookupPreflight.FindUnsupplied("contact", meta, null, ["lead"]);

        var message = Assert.Single(messages);
        Assert.Contains("new_requiredid", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_target_is_flagged_even_when_the_table_is_in_the_run()
    {
        var meta = Meta("account", RequiredLookup("new_requiredid", "account"));

        var messages = RequiredLookupPreflight.FindUnsupplied("account", meta, null, ["account"]);

        Assert.Single(messages);
    }

    [Fact]
    public void Polymorphic_lookup_with_one_target_missing_is_flagged()
    {
        var meta = Meta("task", RequiredLookup("new_requiredid", "account", "contact"));

        var messages = RequiredLookupPreflight.FindUnsupplied("task", meta, null, ["account"]);

        Assert.Single(messages);
    }

    [Fact]
    public void Polymorphic_lookup_with_every_target_created_earlier_needs_no_rule()
    {
        var meta = Meta("task", RequiredLookup("new_requiredid", "account", "contact"));

        Assert.Empty(RequiredLookupPreflight.FindUnsupplied("task", meta, null, ["account", "contact"]));
    }

    [Fact]
    public void Explicit_rule_supplies_the_lookup()
    {
        var meta = Meta("contact", RequiredLookup("new_requiredid", "account"));
        var rules = new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["new_requiredid"] = new ConstantRule(JsonDocument.Parse($"\"{Guid.NewGuid()}\"").RootElement),
        };

        Assert.Empty(RequiredLookupPreflight.FindUnsupplied("contact", meta, rules));
    }

    [Fact]
    public void FindSuppliedByCreatedEarlier_names_only_unruled_lookups_accepted_through_the_run()
    {
        var meta = Meta("contact",
            RequiredLookup("new_requiredid", "account"),
            RequiredLookup("new_externalid", "lead"),
            RequiredLookup("new_ruledid", "account"));
        var rules = new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["new_ruledid"] = new LookupRandomRule(),
        };

        var names = RequiredLookupPreflight.FindSuppliedByCreatedEarlier("contact", meta, rules, ["account"]);

        Assert.Equal(["new_requiredid"], names);
    }
}

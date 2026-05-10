using System.Reflection;

namespace DataGen.Core.Tests;

public class EdgeCaseValidatorTests
{
    private readonly EdgeCaseValidator _validator = new(NullLogger<EdgeCaseValidator>.Instance);

    private static EntityMetadata EmptyEntity(string logicalName = "test_entity")
        => new() { LogicalName = logicalName };

    private static void SetReadOnlyInHierarchy(object obj, string propertyName, object value)
    {
        var type = obj.GetType();
        while (type is not null)
        {
            var prop = type.GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (prop?.CanWrite == true) { prop.SetValue(obj, value); return; }
            var field = type.GetField($"<{propertyName}>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field is not null) { field.SetValue(obj, value); return; }
            type = type.BaseType;
        }
    }

    private static EntityMetadata EntityWithAlternateKey(string fieldName)
    {
        var entity = new EntityMetadata { LogicalName = "account" };

        var key = new EntityKeyMetadata();
        SetReadOnlyInHierarchy(key, "KeyAttributes", new[] { fieldName });
        SetReadOnlyInHierarchy(entity, "Keys", new[] { key });

        return entity;
    }

    // 1. Calculated / rollup / formula
    [Fact]
    public void Validate_CalculatedField_ReturnsSkip()
    {
        var attr = new StringAttributeMetadata { LogicalName = "computedfield", SourceType = 1 };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Skip, result.Action);
    }

    // 2. Auto-number
    [Fact]
    public void Validate_AutoNumberField_ReturnsSkip()
    {
        var attr = new StringAttributeMetadata
        {
            LogicalName = "ticketnumber",
            AutoNumberFormat = "TICKET-{SEQNUM:6}"
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Skip, result.Action);
    }

    // 3. Base currency
    [Fact]
    public void Validate_BaseCurrencyField_ReturnsSkip()
    {
        var attr = new MoneyAttributeMetadata { LogicalName = "revenue_base" };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Skip, result.Action);
    }

    // 4. Image column
    [Fact]
    public void Validate_ImageField_ReturnsSkip()
    {
        var attr = new ImageAttributeMetadata { LogicalName = "entityimage" };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Skip, result.Action);
    }

    // 4b. File column
    [Fact]
    public void Validate_FileField_ReturnsSkip()
    {
        var attr = new FileAttributeMetadata { LogicalName = "document" };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Skip, result.Action);
    }

    // 5. statecode
    [Fact]
    public void Validate_StateCodeField_ReturnsSkip()
    {
        var attr = new StringAttributeMetadata { LogicalName = "statecode" };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Skip, result.Action);
    }

    // 6. statuscode
    [Fact]
    public void Validate_StatusCodeField_ReturnsSkip()
    {
        var attr = new StringAttributeMetadata { LogicalName = "statuscode" };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Skip, result.Action);
    }

    // 7. Picklist with no options
    [Fact]
    public void Validate_PicklistNoOptions_ReturnsFail()
    {
        var attr = new PicklistAttributeMetadata
        {
            LogicalName = "optionfield",
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection())
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Fail, result.Action);
    }

    // 7b. Picklist with options — normal generate
    [Fact]
    public void Validate_PicklistWithOptions_ReturnsGenerate()
    {
        var options = new OptionMetadataCollection();
        options.Add(new OptionMetadata(new Label("Option1", 1033), 1));
        var attr = new PicklistAttributeMetadata
        {
            LogicalName = "optionfield",
            OptionSet = new OptionSetMetadata(options)
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Generate, result.Action);
    }

    // 8. Multi-select no options
    [Fact]
    public void Validate_MultiSelectNoOptions_ReturnsFail()
    {
        var attr = new MultiSelectPicklistAttributeMetadata
        {
            LogicalName = "multifield",
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection())
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Fail, result.Action);
    }

    // 8b. Multi-select with options
    [Fact]
    public void Validate_MultiSelectWithOptions_ReturnsSpecialHandling()
    {
        var options = new OptionMetadataCollection();
        options.Add(new OptionMetadata(new Label("A", 1033), 1));
        var attr = new MultiSelectPicklistAttributeMetadata
        {
            LogicalName = "multifield",
            OptionSet = new OptionSetMetadata(options)
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.SpecialHandling, result.Action);
        Assert.Equal("MultiSelect", result.HandlingCategory);
    }

    // 9. Currency (Money)
    [Fact]
    public void Validate_MoneyField_ReturnsSpecialHandling()
    {
        var attr = new MoneyAttributeMetadata { LogicalName = "budget" };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.SpecialHandling, result.Action);
        Assert.Equal("CurrencyValidation", result.HandlingCategory);
    }

    // 10. DateTime
    [Fact]
    public void Validate_DateTimeField_ReturnsSpecialHandling()
    {
        var attr = new DateTimeAttributeMetadata
        {
            LogicalName = "createdon",
            DateTimeBehavior = DateTimeBehavior.DateOnly
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.SpecialHandling, result.Action);
        Assert.StartsWith("DateTime_", result.HandlingCategory);
    }

    // 11. Alternate key field
    [Fact]
    public void Validate_AlternateKeyField_ReturnsSpecialHandling()
    {
        var entity = EntityWithAlternateKey("accountnumber");
        var attr = new StringAttributeMetadata { LogicalName = "accountnumber" };
        var result = _validator.Validate(attr, entity);
        Assert.Equal(FieldAction.SpecialHandling, result.Action);
        Assert.Equal("AlternateKeyUniqueness", result.HandlingCategory);
    }

    // 12. Rich text memo
    [Fact]
    public void Validate_RichTextMemo_ReturnsSpecialHandling()
    {
        var attr = new MemoAttributeMetadata
        {
            LogicalName = "richtext",
            FormatName = MemoFormatName.RichText
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.SpecialHandling, result.Action);
        Assert.Equal("RichText", result.HandlingCategory);
    }

    // 13. Polymorphic lookup (multiple targets)
    [Fact]
    public void Validate_PolymorphicLookup_ReturnsSpecialHandling()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "customerid",
            Targets = ["account", "contact"]
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.SpecialHandling, result.Action);
        Assert.Equal("PolymorphicLookup", result.HandlingCategory);
    }

    // 13b. Owner lookup
    [Fact]
    public void Validate_OwnerLookup_ReturnsSpecialHandling()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "ownerid",
            Targets = ["systemuser"]
        };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.SpecialHandling, result.Action);
        Assert.Equal("OwnerLookup", result.HandlingCategory);
    }

    // Normal field → Generate
    [Fact]
    public void Validate_NormalStringField_ReturnsGenerate()
    {
        var attr = new StringAttributeMetadata { LogicalName = "customfield" };
        var result = _validator.Validate(attr, EmptyEntity());
        Assert.Equal(FieldAction.Generate, result.Action);
    }

    [Fact]
    public void Validate_ThrowsOnNullAttr()
    {
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(null!, EmptyEntity()));
    }

    [Fact]
    public void Validate_ThrowsOnNullEntity()
    {
        var attr = new StringAttributeMetadata { LogicalName = "field" };
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(attr, null!));
    }
}

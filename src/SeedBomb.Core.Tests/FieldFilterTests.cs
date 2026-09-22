namespace SeedBomb.Core.Tests;

public class FieldFilterTests
{
    [Fact]
    public void ShouldGenerateField_ValidStringField_ReturnsTrue()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true };
        Assert.True(FieldFilter.ShouldGenerateField(attr));
    }

    [Fact]
    public void ShouldGenerateField_NotValidForCreate_ReturnsFalse()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = false };
        Assert.False(FieldFilter.ShouldGenerateField(attr));
    }

    [Fact]
    public void ShouldGenerateField_NullIsValidForCreate_ReturnsTrue()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = null };
        Assert.True(FieldFilter.ShouldGenerateField(attr));
    }

    [Fact]
    public void IsCalculatedOrFormula_SourceTypeGreaterThanZero_ReturnsTrue()
    {
        var attr = new StringAttributeMetadata { SourceType = 1 };
        Assert.True(FieldFilter.IsCalculatedOrFormula(attr));
    }

    [Fact]
    public void IsCalculatedOrFormula_SourceTypeZero_ReturnsFalse()
    {
        var attr = new StringAttributeMetadata { SourceType = 0 };
        Assert.False(FieldFilter.IsCalculatedOrFormula(attr));
    }

    [Fact]
    public void IsAutoNumber_WithAutoNumberFormat_ReturnsTrue()
    {
        var attr = new StringAttributeMetadata { AutoNumberFormat = "PREFIX-{SEQNUM:5}" };
        Assert.True(FieldFilter.IsAutoNumber(attr));
    }

    [Fact]
    public void IsAutoNumber_NoAutoNumberFormat_ReturnsFalse()
    {
        var attr = new StringAttributeMetadata { AutoNumberFormat = null };
        Assert.False(FieldFilter.IsAutoNumber(attr));
    }

    [Fact]
    public void IsBaseCurrencyField_EndsWithBase_ReturnsTrue()
    {
        Assert.True(FieldFilter.IsBaseCurrencyField("amount_base"));
    }

    [Fact]
    public void IsBaseCurrencyField_DoesNotEndWithBase_ReturnsFalse()
    {
        Assert.False(FieldFilter.IsBaseCurrencyField("amount"));
    }

    [Fact]
    public void IsBaseCurrencyField_NullName_ReturnsFalse()
    {
        Assert.False(FieldFilter.IsBaseCurrencyField(null));
    }

    [Fact]
    public void IsFileOrImageColumn_ImageMetadata_ReturnsTrue()
    {
        var attr = new ImageAttributeMetadata();
        Assert.True(FieldFilter.IsFileOrImageColumn(attr));
    }

    [Fact]
    public void IsFileOrImageColumn_FileMetadata_ReturnsTrue()
    {
        var attr = new FileAttributeMetadata();
        Assert.True(FieldFilter.IsFileOrImageColumn(attr));
    }

    [Fact]
    public void IsFileOrImageColumn_StringMetadata_ReturnsFalse()
    {
        var attr = new StringAttributeMetadata();
        Assert.False(FieldFilter.IsFileOrImageColumn(attr));
    }

    [Fact]
    public void IsStateCode_StateCodeField_ReturnsTrue()
    {
        var attr = new StringAttributeMetadata { LogicalName = "statecode" };
        Assert.True(FieldFilter.IsStateCode(attr));
    }

    [Fact]
    public void IsStateCode_OtherField_ReturnsFalse()
    {
        var attr = new StringAttributeMetadata { LogicalName = "statuscode" };
        Assert.False(FieldFilter.IsStateCode(attr));
    }

    [Fact]
    public void ShouldGenerateField_CalculatedField_ReturnsFalse()
    {
        var attr = new StringAttributeMetadata { LogicalName = "computed", IsValidForCreate = true, SourceType = 1 };
        Assert.False(FieldFilter.ShouldGenerateField(attr));
    }

    [Fact]
    public void ShouldGenerateField_BaseCurrencyField_ReturnsFalse()
    {
        var attr = new MoneyAttributeMetadata { LogicalName = "revenue_base", IsValidForCreate = true };
        Assert.False(FieldFilter.ShouldGenerateField(attr));
    }

    [Fact]
    public void ShouldGenerateField_StateCodeField_ReturnsFalse()
    {
        var attr = new StringAttributeMetadata { LogicalName = "statecode", IsValidForCreate = true };
        Assert.False(FieldFilter.ShouldGenerateField(attr));
    }

    [Fact]
    public void ShouldGenerateField_SystemRequiredNonStringField_ReturnsFalse()
    {
        var attr = new DateTimeAttributeMetadata
        {
            LogicalName = "overriddencreatedon",
            IsValidForCreate = true,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired)
        };
        Assert.False(FieldFilter.ShouldGenerateField(attr));
    }

    [Fact]
    public void IsSystemRequired_StringAttribute_IsExempted_ReturnsFalse()
    {
        var attr = new StringAttributeMetadata
        {
            LogicalName = "lastname",
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired)
        };
        Assert.False(FieldFilter.IsSystemRequired(attr));
    }

    [Fact]
    public void IsSystemRequired_DateTimeAttribute_ReturnsTrue()
    {
        var attr = new DateTimeAttributeMetadata
        {
            LogicalName = "createdon",
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired)
        };
        Assert.True(FieldFilter.IsSystemRequired(attr));
    }

    [Fact]
    public void ShouldGenerateField_SystemRequiredStringField_ReturnsTrue()
    {
        var attr = new StringAttributeMetadata
        {
            LogicalName = "lastname",
            IsValidForCreate = true,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired)
        };
        Assert.True(FieldFilter.ShouldGenerateField(attr));
    }

    [Fact]
    public void ShouldGenerateField_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => FieldFilter.ShouldGenerateField(null!));
    }
}

using Bogus;

namespace DataGen.Core.Tests;

public class StatusFieldGeneratorTests
{
    private readonly StatusFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void CanGenerate_StatusMetadata_ReturnsTrue()
        => Assert.True(_gen.CanGenerate(new StatusAttributeMetadata()));

    [Fact]
    public void CanGenerate_StringMetadata_ReturnsFalse()
        => Assert.False(_gen.CanGenerate(new StringAttributeMetadata()));

    [Fact]
    public void Generate_WithStatusOptions_ReturnsOptionSetValue()
    {
        var options = new OptionMetadataCollection();
        options.Add(new OptionMetadata(new Label("Active", 1033), 1));
        options.Add(new OptionMetadata(new Label("Inactive", 1033), 2));

        var attr = new StatusAttributeMetadata { LogicalName = "statuscode" };
        attr.OptionSet = new OptionSetMetadata(options);

        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<OptionSetValue>(result);
        var value = ((OptionSetValue)result!).Value;
        Assert.True(value is 1 or 2);
    }

    [Fact]
    public void Generate_EmptyOptions_ReturnsNull()
    {
        var attr = new StatusAttributeMetadata { LogicalName = "statuscode" };
        attr.OptionSet = new OptionSetMetadata(new OptionMetadataCollection());

        var result = _gen.Generate(attr, _faker, _pool);
        Assert.Null(result);
    }

    [Fact]
    public void Generate_NullOptionSet_ReturnsNull()
    {
        var attr = new StatusAttributeMetadata { LogicalName = "statuscode", OptionSet = null };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.Null(result);
    }
}

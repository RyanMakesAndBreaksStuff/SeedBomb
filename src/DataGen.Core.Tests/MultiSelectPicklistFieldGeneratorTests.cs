using Bogus;

namespace DataGen.Core.Tests;

public class MultiSelectPicklistFieldGeneratorTests
{
    private readonly MultiSelectPicklistFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    private static MultiSelectPicklistAttributeMetadata WithOptions(params int[] values)
    {
        var options = new OptionMetadataCollection();
        foreach (var v in values)
            options.Add(new OptionMetadata(new Label("Option", 1033), v));
        return new MultiSelectPicklistAttributeMetadata
        {
            LogicalName = "multifield",
            OptionSet = new OptionSetMetadata(options)
        };
    }


    [Fact]
    public void Generate_WithOptions_ReturnsOptionSetValueCollection()
    {
        var attr = WithOptions(1, 2, 3, 4, 5);
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<OptionSetValueCollection>(result);
    }

    [Fact]
    public void Generate_WithOptions_SelectsOneToThreeValues()
    {
        var attr = WithOptions(1, 2, 3, 4, 5);
        var result = (OptionSetValueCollection)_gen.Generate(attr, _faker, _pool)!;
        Assert.True(result.Count >= 1 && result.Count <= 3,
            $"Expected 1-3 values, got {result.Count}");
    }

    [Fact]
    public void Generate_EmptyOptions_ReturnsNull()
    {
        var attr = new MultiSelectPicklistAttributeMetadata
        {
            LogicalName = "multifield",
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection())
        };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.Null(result);
    }

    [Fact]
    public void Generate_NullOptionSet_ReturnsNull()
    {
        var attr = new MultiSelectPicklistAttributeMetadata { LogicalName = "multifield", OptionSet = null };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.Null(result);
    }
}

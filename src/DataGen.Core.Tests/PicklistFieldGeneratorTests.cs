using Bogus;

namespace DataGen.Core.Tests;

public class PicklistFieldGeneratorTests
{
    private readonly PicklistFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    private static PicklistAttributeMetadata WithOptions(params int[] values)
    {
        var options = new OptionMetadataCollection();
        foreach (var v in values)
            options.Add(new OptionMetadata(new Label("Option", 1033), v));
        return new PicklistAttributeMetadata
        {
            LogicalName = "optionfield",
            OptionSet = new OptionSetMetadata(options)
        };
    }


    [Fact]
    public void Generate_WithOptions_ReturnsValidOptionSetValue()
    {
        var attr = WithOptions(1, 2, 3);
        var result = (OptionSetValue)_gen.Generate(attr, _faker, _pool)!;

        Assert.True(result.Value is 1 or 2 or 3);
    }

    [Fact]
    public void Generate_EmptyOptions_ReturnsNull()
    {
        var attr = new PicklistAttributeMetadata
        {
            LogicalName = "optionfield",
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection())
        };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.Null(result);
    }

    [Fact]
    public void Generate_NullOptionSet_ReturnsNull()
    {
        var attr = new PicklistAttributeMetadata { LogicalName = "optionfield", OptionSet = null };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.Null(result);
    }
}

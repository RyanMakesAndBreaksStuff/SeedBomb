using Bogus;

namespace SeedBomb.Core.Tests;

public class BooleanFieldGeneratorTests
{
    private readonly BooleanFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);


    [Fact]
    public void Generate_ReturnsBoolValue()
    {
        var attr = new BooleanAttributeMetadata { LogicalName = "donotcontact" };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<bool>(result);
    }
}

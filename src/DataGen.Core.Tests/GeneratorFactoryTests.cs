using Bogus;

namespace DataGen.Core.Tests;

public class GeneratorFactoryTests
{
    private readonly GeneratorFactory _factory = new(NullLogger<GeneratorFactory>.Instance);
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    [Fact]
    public void Generate_StringAttribute_ReturnsString()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", MaxLength = 100 };
        var result = _factory.Generate(attr, _faker, _pool);
        Assert.IsType<string>(result);
    }

    [Fact]
    public void Generate_IntegerAttribute_ReturnsInt()
    {
        var attr = new IntegerAttributeMetadata { LogicalName = "age", MinValue = 0, MaxValue = 100 };
        var result = _factory.Generate(attr, _faker, _pool);
        Assert.IsType<int>(result);
    }

    [Fact]
    public void Generate_BooleanAttribute_ReturnsBool()
    {
        var attr = new BooleanAttributeMetadata { LogicalName = "isactive" };
        var result = _factory.Generate(attr, _faker, _pool);
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void Generate_UniqueIdentifierAttribute_ReturnsGuid()
    {
        var attr = new UniqueIdentifierAttributeMetadata { LogicalName = "entityid" };
        var result = _factory.Generate(attr, _faker, _pool);
        Assert.IsType<Guid>(result);
        Assert.NotEqual(Guid.Empty, (Guid)result!);
    }

    [Fact]
    public void Generate_UnknownAttributeType_ReturnsNull()
    {
        // BigIntAttributeMetadata has no registered generator
        var attr = new BigIntAttributeMetadata { LogicalName = "bigfield" };
        var result = _factory.Generate(attr, _faker, _pool);
        Assert.Null(result);
    }

    [Fact]
    public void Generate_ThrowsOnNullAttr()
    {
        Assert.Throws<ArgumentNullException>(() => _factory.Generate(null!, _faker, _pool));
    }

    [Fact]
    public void Generate_ThrowsOnNullFaker()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name" };
        Assert.Throws<ArgumentNullException>(() => _factory.Generate(attr, null!, _pool));
    }

    [Fact]
    public void Generate_ThrowsOnNullPool()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name" };
        Assert.Throws<ArgumentNullException>(() => _factory.Generate(attr, _faker, null!));
    }

    [Fact]
    public void RegisterGenerator_OverrideExisting_UsesNewGenerator()
    {
        var factory = new GeneratorFactory(NullLogger<GeneratorFactory>.Instance);
        var mockGenerator = new Mock<IFieldGenerator>();
        mockGenerator.Setup(g => g.Generate(It.IsAny<AttributeMetadata>(), It.IsAny<Faker>(), It.IsAny<DataverseRecordPool>()))
            .Returns("custom_value");

        factory.RegisterGenerator<StringAttributeMetadata>(mockGenerator.Object);

        var attr = new StringAttributeMetadata { LogicalName = "name" };
        var result = factory.Generate(attr, _faker, _pool);

        Assert.Equal("custom_value", result);
    }
}

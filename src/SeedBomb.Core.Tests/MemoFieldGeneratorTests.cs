using Bogus;

namespace SeedBomb.Core.Tests;

public class MemoFieldGeneratorTests
{
    private readonly MemoFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);


    [Fact]
    public void Generate_PlainMemo_ReturnsString()
    {
        var attr = new MemoAttributeMetadata { LogicalName = "description", MaxLength = 2000 };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<string>(result);
        Assert.NotEmpty((string)result!);
    }

    [Fact]
    public void Generate_RichTextField_ReturnsHtmlParagraph()
    {
        var attr = new MemoAttributeMetadata
        {
            LogicalName = "richtext",
            FormatName = MemoFormatName.RichText
        };
        var result = (string)_gen.Generate(attr, _faker, _pool)!;
        Assert.StartsWith("<p>", result);
        Assert.Contains("</p>", result);
    }

    [Fact]
    public void Generate_RespectsMaxLength()
    {
        var attr = new MemoAttributeMetadata { LogicalName = "description", MaxLength = 20 };
        var result = (string)_gen.Generate(attr, _faker, _pool)!;
        Assert.True(result.Length <= 20, $"Length {result.Length} exceeds MaxLength 20");
    }
}

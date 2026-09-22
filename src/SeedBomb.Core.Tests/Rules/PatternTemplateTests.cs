using SeedBomb.Core.Rules;

namespace SeedBomb.Core.Tests.Rules;

public class PatternTemplateTests
{
    [Fact]
    public void Expands_seq_with_padding()
    {
        var t = PatternTemplate.Parse("dg+{seq:0000}@test.invalid");
        Assert.Equal("dg+0001@test.invalid", t.Expand(rowIndex: 0, random: _ => "X", runId: "r"));
    }

    [Fact]
    public void Random_token_uses_supplied_source()
    {
        var t = PatternTemplate.Parse("u-{random:8}");
        Assert.Equal("u-abcdefgh", t.Expand(0, n => "abcdefgh"[..n], "r"));
    }

    [Fact]
    public void RunId_token_substitutes() =>
        Assert.Equal("m-R1", PatternTemplate.Parse("m-{runId}").Expand(0, _ => "", "R1"));

    [Theory]
    [InlineData("{eval:1+1}")]
    [InlineData("{seq:zz}")]
    [InlineData("{unclosed")]
    public void Unknown_or_malformed_tokens_throw(string template)
        => Assert.Throws<FormatException>(() => PatternTemplate.Parse(template));

    [Fact]
    public void MaxExpandedLength_uses_worst_case()
    {
        // {seq:0000} worst case for count 500 is 4 chars ("0500"), {random:8} is 8.
        var t = PatternTemplate.Parse("a{seq:0000}b{random:8}");
        Assert.Equal(1 + 4 + 1 + 8, t.MaxExpandedLength(recordCount: 500, runIdLength: 0));
    }
}

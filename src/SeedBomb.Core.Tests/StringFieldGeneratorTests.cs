using Bogus;

namespace SeedBomb.Core.Tests;

public class StringFieldGeneratorTests
{
    private readonly StringFieldGenerator _gen = new();
    private readonly DataverseRecordPool _pool = new();
    private readonly Faker _faker = DeterministicFaker.Create(42, 0);

    private string Generate(string fieldName, int maxLength = 200)
    {
        var attr = new StringAttributeMetadata { LogicalName = fieldName, MaxLength = maxLength };
        return (string)_gen.Generate(attr, _faker, _pool)!;
    }

    private string GenerateWithFormat(string fieldName, StringFormatName format, int maxLength = 200)
    {
        var attr = new StringAttributeMetadata { LogicalName = fieldName, MaxLength = maxLength, FormatName = format };
        return (string)_gen.Generate(attr, _faker, _pool)!;
    }


    [Theory]
    [InlineData("emailaddress1")]
    [InlineData("emailaddress")]
    [InlineData("internalemail")]
    public void Generate_EmailField_ContainsAtSign(string fieldName)
    {
        var value = Generate(fieldName);
        Assert.Contains("@", value);
    }

    [Theory]
    [InlineData("telephone1")]
    [InlineData("mobilephone")]
    [InlineData("fax")]
    public void Generate_PhoneField_ReturnsNonEmpty(string fieldName)
    {
        var value = Generate(fieldName);
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Generate_FirstNameField_ReturnsNonEmpty()
    {
        var value = Generate("firstname");
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Generate_LastNameField_ReturnsNonEmpty()
    {
        var value = Generate("lastname");
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Generate_CityField_ReturnsNonEmpty()
    {
        var value = Generate("address1_city");
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Generate_CountryField_ReturnsNonEmpty()
    {
        var value = Generate("address1_country");
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Generate_ZipField_ReturnsNonEmpty()
    {
        var value = Generate("address1_postalcode");
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Generate_UrlField_ContainsProtocol()
    {
        var value = Generate("websiteurl");
        Assert.Matches(@"https?://", value);
    }

    [Fact]
    public void Generate_FormatEmail_TakesPriority()
    {
        var value = GenerateWithFormat("customtext", StringFormatName.Email);
        Assert.Contains("@", value);
    }

    [Fact]
    public void Generate_DescriptionField_ReturnsNonEmpty()
    {
        var value = Generate("description");
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Generate_CodeField_ReturnsUpperAlphanumeric()
    {
        var value = Generate("accountnumber");
        Assert.NotEmpty(value);
        Assert.Matches("^[A-Z0-9]+$", value);
    }

    [Fact]
    public void Generate_FallbackField_ReturnsNonEmpty()
    {
        var value = Generate("customfield123");
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Generate_RespectsMaxLength()
    {
        // Use a very short max length
        var attr = new StringAttributeMetadata { LogicalName = "description", MaxLength = 5 };
        var result = (string)_gen.Generate(attr, _faker, _pool)!;
        Assert.True(result.Length <= 5, $"Length {result.Length} exceeds MaxLength 5");
    }

    [Fact]
    public void Generate_ReturnsString()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", MaxLength = 100 };
        var result = _gen.Generate(attr, _faker, _pool);
        Assert.IsType<string>(result);
    }
}

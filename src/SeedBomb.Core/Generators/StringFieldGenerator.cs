using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace SeedBomb.Core.Generators;

/// <summary>
/// Generates fake string values with field-name heuristics for realistic data.
/// </summary>
internal sealed class StringFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var stringMeta = (StringAttributeMetadata)metadata;
        var maxLength = stringMeta.MaxLength ?? 100;
        var name = metadata.LogicalName ?? string.Empty;

        var value = stringMeta.FormatName?.Value switch
        {
            "Email" => faker.Internet.Email(),
            "Url" => faker.Internet.Url(),
            "Phone" => faker.Phone.PhoneNumber(),
            "TickerSymbol" => faker.Random.AlphaNumeric(4).ToUpperInvariant(),
            _ => GenerateByFieldName(name, faker)
        };
        return Truncate(value, maxLength);
    }

    private static string GenerateByFieldName(string fieldName, Faker faker)
    {
        var lower = fieldName.ToLowerInvariant();

        if (lower.Contains("email") || lower.Contains("emailaddress"))
            return faker.Internet.Email();

        if (lower.Contains("phone") || lower.Contains("telephone") || lower.Contains("fax"))
            return faker.Phone.PhoneNumber();

        if (lower.Contains("firstname") || lower.Contains("first_name"))
            return faker.Name.FirstName();

        if (lower.Contains("lastname") || lower.Contains("last_name") || lower.Contains("surname"))
            return faker.Name.LastName();

        if (lower.Contains("fullname") || lower.Contains("full_name") || lower.Contains("name") && lower.Contains("company"))
            return faker.Company.CompanyName();

        if (lower.Contains("name"))
            return faker.Name.FullName();

        if (lower.Contains("city"))
            return faker.Address.City();

        if (lower.Contains("state") || lower.Contains("province"))
            return faker.Address.State();

        if (lower.Contains("country"))
            return faker.Address.Country();

        if (lower.Contains("zip") || lower.Contains("postal"))
            return faker.Address.ZipCode();

        if (lower.Contains("address") || lower.Contains("street") || lower.Contains("line1") || lower.Contains("line2"))
            return faker.Address.StreetAddress();

        if (lower.Contains("url") || lower.Contains("website") || lower.Contains("webpage"))
            return faker.Internet.Url();

        if (lower.Contains("description") || lower.Contains("comment") || lower.Contains("note"))
            return faker.Lorem.Sentence();

        if (lower.Contains("title") || lower.Contains("subject"))
            return faker.Lorem.Sentence(3);

        if (lower.Contains("code") || lower.Contains("number") || lower.Contains("ticker"))
            return faker.Random.AlphaNumeric(8).ToUpperInvariant();

        return faker.Lorem.Word();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length > maxLength ? value[..maxLength] : value;
}

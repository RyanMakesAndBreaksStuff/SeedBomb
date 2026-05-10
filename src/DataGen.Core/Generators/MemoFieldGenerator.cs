using Bogus;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake memo (multi-line text) values.
/// </summary>
internal sealed class MemoFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is MemoAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var memoMeta = (MemoAttributeMetadata)metadata;
        var maxLength = memoMeta.MaxLength ?? 2000;

        if (string.Equals(memoMeta.FormatName?.Value, "RichText", StringComparison.OrdinalIgnoreCase))
        {
            var html = $"<p>{System.Net.WebUtility.HtmlEncode(faker.Lorem.Paragraph())}</p>";
            return html.Length > maxLength ? html[..maxLength] : html;
        }

        var text = faker.Lorem.Paragraphs(faker.Random.Int(1, 3));
        return text.Length > maxLength ? text[..maxLength] : text;
    }
}

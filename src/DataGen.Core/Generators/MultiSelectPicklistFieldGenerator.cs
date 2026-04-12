using Bogus;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake multi-select picklist values by selecting one or more options.
/// </summary>
internal sealed class MultiSelectPicklistFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is MultiSelectPicklistAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var msMeta = (MultiSelectPicklistAttributeMetadata)metadata;
        var options = msMeta.OptionSet?.Options;

        if (options is null || options.Count == 0)
            return null;

        // Pick 1 to min(3, count) options
        var optionList = options.Cast<OptionMetadata>().ToList();
        var count = faker.Random.Int(1, Math.Min(3, optionList.Count));
        var selected = faker.PickRandom(optionList, count)
            .Where(o => o.Value.HasValue)
            .Select(o => o.Value!.Value)
            .ToArray();

        return selected.Length > 0 ? new OptionSetValueCollection(selected.Select(v => new OptionSetValue(v)).ToList()) : null;
    }
}

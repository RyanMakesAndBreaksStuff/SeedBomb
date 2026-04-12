using Bogus;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake picklist (option set) values by selecting from available options.
/// </summary>
internal sealed class PicklistFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is PicklistAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var picklistMeta = (PicklistAttributeMetadata)metadata;
        var options = picklistMeta.OptionSet?.Options;

        if (options is null || options.Count == 0)
            return null;

        var optionList = options.Cast<OptionMetadata>().ToList();
        var option = faker.PickRandom(optionList);
        return option.Value.HasValue ? new OptionSetValue(option.Value.Value) : null;
    }
}

using Bogus;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Generators;

/// <summary>
/// Generates fake status (statuscode) values. State (statecode) is handled by the platform
/// and should not be set directly; this generator covers statuscode only.
/// </summary>
internal sealed class StatusFieldGenerator : IFieldGenerator
{
    /// <inheritdoc/>
    public bool CanGenerate(AttributeMetadata metadata)
        => metadata is StatusAttributeMetadata;

    /// <inheritdoc/>
    public object? Generate(AttributeMetadata metadata, Faker faker, DataverseRecordPool pool)
    {
        var statusMeta = (StatusAttributeMetadata)metadata;
        var options = statusMeta.OptionSet?.Options;

        if (options is null || options.Count == 0)
            return null;

        // Pick from available status options
        var optionList = options.Cast<OptionMetadata>().ToList();
        var option = faker.PickRandom(optionList);
        return option.Value.HasValue ? new OptionSetValue(option.Value.Value) : null;
    }
}

using Bogus;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using System.Collections.Concurrent;

namespace DataGen.Core.Generators;

/// <summary>
/// Maps Dataverse attribute metadata types to their corresponding field generators
/// and dispatches generation requests.
/// </summary>
public class GeneratorFactory
{
    private readonly Dictionary<Type, IFieldGenerator> _generators = new();
    private readonly ConcurrentDictionary<Type, bool> _typeMatchCache = new();
    private readonly ILogger<GeneratorFactory> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeneratorFactory"/> class
    /// with the default set of field generators.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public GeneratorFactory(ILogger<GeneratorFactory> logger)
    {
        _logger = logger;

        RegisterGenerator<StringAttributeMetadata>(new StringFieldGenerator());
        RegisterGenerator<MoneyAttributeMetadata>(new MoneyFieldGenerator());
        RegisterGenerator<PicklistAttributeMetadata>(new PicklistFieldGenerator());
        RegisterGenerator<LookupAttributeMetadata>(new LookupFieldGenerator());
        RegisterGenerator<DateTimeAttributeMetadata>(new DateTimeFieldGenerator());
        RegisterGenerator<MultiSelectPicklistAttributeMetadata>(new MultiSelectPicklistFieldGenerator());
        RegisterGenerator<BooleanAttributeMetadata>(new BooleanFieldGenerator());
        RegisterGenerator<IntegerAttributeMetadata>(new IntegerFieldGenerator());
        RegisterGenerator<DecimalAttributeMetadata>(new DecimalFieldGenerator());
        RegisterGenerator<DoubleAttributeMetadata>(new DoubleFieldGenerator());
        RegisterGenerator<MemoAttributeMetadata>(new MemoFieldGenerator());
        RegisterGenerator<UniqueIdentifierAttributeMetadata>(new UniqueIdentifierFieldGenerator());
        RegisterGenerator<BigIntAttributeMetadata>(new BigIntFieldGenerator());
    }

    /// <summary>
    /// Registers a field generator for a specific attribute metadata type.
    /// </summary>
    /// <typeparam name="TMetadata">The attribute metadata type to handle.</typeparam>
    /// <param name="generator">The generator instance.</param>
    public void RegisterGenerator<TMetadata>(IFieldGenerator generator) where TMetadata : AttributeMetadata
    {
        ArgumentNullException.ThrowIfNull(generator);
        _generators[typeof(TMetadata)] = generator;
    }

    /// <summary>
    /// Generates a fake value for the given attribute using the appropriate generator.
    /// </summary>
    /// <param name="attr">The attribute metadata.</param>
    /// <param name="faker">The seeded Faker instance.</param>
    /// <param name="pool">The record pool for lookup resolution.</param>
    /// <returns>The generated value, or null if no generator is registered.</returns>
    public object? Generate(AttributeMetadata attr, Faker faker, DataverseRecordPool pool)
    {
        ArgumentNullException.ThrowIfNull(attr);
        ArgumentNullException.ThrowIfNull(faker);
        ArgumentNullException.ThrowIfNull(pool);

        var type = attr.GetType();
        if (_typeMatchCache.TryGetValue(type, out var hasMatch) && !hasMatch)
        {
            _logger.LogDebug("No generator registered for {AttributeType}, skipping {FieldName}",
                type.Name, attr.LogicalName);
            return null;
        }

        // Walk inheritance chain to handle SDK subtype metadata (e.g. CustomerAttributeMetadata → LookupAttributeMetadata)
        var current = type;
        while (current != null && current != typeof(object))
        {
            if (_generators.TryGetValue(current, out var generator))
            {
                _logger.LogDebug("Generating value for {FieldName} using {GeneratorType} (matched via {MatchedType})",
                    attr.LogicalName, generator.GetType().Name, current.Name);
                _typeMatchCache[type] = true;
                return generator.Generate(attr, faker, pool);
            }
            current = current.BaseType;
        }

        if (_typeMatchCache.TryAdd(type, false))
            _logger.LogWarning("No generator registered for {AttributeType}, skipping {FieldName}", type.Name, attr.LogicalName);
        else
            _logger.LogDebug("No generator registered for {AttributeType}, skipping {FieldName}", type.Name, attr.LogicalName);
        return null;
    }
}
